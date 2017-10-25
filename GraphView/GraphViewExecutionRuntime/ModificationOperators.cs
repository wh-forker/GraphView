﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Newtonsoft.Json.Linq;
using static GraphView.DocumentDBKeywords;

namespace GraphView
{
    internal abstract class ModificationBaseOperator : GraphViewExecutionOperator
    {
        protected GraphViewCommand Command;
        protected GraphViewExecutionOperator InputOperator;

        protected ModificationBaseOperator(GraphViewExecutionOperator inputOp, GraphViewCommand command)
        {
            this.InputOperator = inputOp;
            this.Command = command;
            this.Open();
        }

        internal abstract RawRecord DataModify(RawRecord record);

        public override RawRecord Next()
        {
            RawRecord srcRecord = null;

            while (InputOperator.State() && (srcRecord = InputOperator.Next()) != null)
            {
                RawRecord result = DataModify(srcRecord);
                if (result == null) continue;

                RawRecord resultRecord = new RawRecord(srcRecord);
                resultRecord.Append(result);

                return resultRecord;
            }

            Close();
            return null;
        }

        public override void ResetState()
        {
            InputOperator.ResetState();
            Open();
        }

    }

    internal class AddVOperator : ModificationBaseOperator
    {
        private readonly JObject _vertexDocument;
        private readonly List<string> _projectedFieldList; 

        public AddVOperator(GraphViewExecutionOperator inputOp, GraphViewCommand command, JObject vertexDocument, List<string> projectedFieldList)
            : base(inputOp, command)
        {
            this._vertexDocument = vertexDocument;
            this._projectedFieldList = projectedFieldList;
        }

        internal override RawRecord DataModify(RawRecord record)
        {
            JObject vertexObject = (JObject)this._vertexDocument.DeepClone();

            string vertexId;
            if (vertexObject[KW_DOC_ID] == null) {
                vertexId = GraphViewConnection.GenerateDocumentId();
                vertexObject[KW_DOC_ID] = vertexId;
            }
            else {
                // Only string id is supported!
                // Assume user will not specify duplicated ids
                Debug.Assert(vertexObject[KW_DOC_ID] is JValue);
                Debug.Assert(((JValue)vertexObject[KW_DOC_ID]).Type == JTokenType.String);

                vertexId = (string)vertexObject[KW_DOC_ID];
            }

            Debug.Assert(vertexObject[KW_DOC_PARTITION] == null);
            if (this.Command.Connection.PartitionPathTopLevel == KW_DOC_PARTITION) {

                // Now the collection is created via GraphAPI

                if (vertexObject[this.Command.Connection.RealPartitionKey] == null) {
                    throw new GraphViewException($"AddV: Parition key '{this.Command.Connection.RealPartitionKey}' must be provided.");
                }

                // Special treat "id" or "label" specified as partition key
                JValue partition;
                if (this.Command.Connection.RealPartitionKey == KW_DOC_ID ||
                    this.Command.Connection.RealPartitionKey == KW_VERTEX_LABEL)
                {
                    partition = (JValue)(string)vertexObject[this.Command.Connection.RealPartitionKey];
                }
                else {
                    JValue value = (JValue)vertexObject[this.Command.Connection.RealPartitionKey];
                    partition = value;
                }

                vertexObject[KW_DOC_PARTITION] = partition;
            }

            VertexField vertexField;

            if (this.Command.InLazyMode)
            {
                vertexObject[DocumentDBKeywords.KW_DOC_ETAG] = DateTimeOffset.Now.ToUniversalTime().ToString();
                vertexField = this.Command.VertexCache.AddOrUpdateVertexField(vertexId, vertexObject);
                DeltaLogAddVertex log = new DeltaLogAddVertex();
                this.Command.VertexCache.AddOrUpdateVertexDelta(vertexField, log);
            }
            else
            {
                //
                // NOTE: We don't check whether the partition key exists. Let DocDB do it.
                // If the vertex doesn't have the specified partition key, a DocumentClientException will be thrown.
                //
                try
                {
                    this.Command.Connection.CreateDocumentAsync(vertexObject, this.Command).Wait();
                }
                catch (AggregateException ex)
                {
                    throw new GraphViewException("Error when uploading the vertex", ex.InnerException);
                }
                vertexField = this.Command.VertexCache.AddOrUpdateVertexField(vertexId, vertexObject);
            }

            RawRecord result = new RawRecord();

            foreach (string fieldName in _projectedFieldList)
            {
                FieldObject fieldValue = vertexField[fieldName];

                result.Append(fieldValue);
            }

            return result;
        }

    }

    internal class DropOperator : ModificationBaseOperator
    {
        private readonly int dropTargetIndex;
        private readonly GraphViewExecutionOperator dummyInputOp;

        public DropOperator(GraphViewExecutionOperator dummyInputOp, GraphViewCommand command, int dropTargetIndex)
            : base(dummyInputOp, command)
        {
            this.dropTargetIndex = dropTargetIndex;
            this.dummyInputOp = dummyInputOp;
        }

        private void DropVertex(VertexField vertexField)
        {
            RawRecord record = new RawRecord();
            record.Append(new StringField(vertexField.VertexId));  // nodeIdIndex
            DropNodeOperator op = new DropNodeOperator(this.dummyInputOp, this.Command, 0);
            op.DataModify(record);

            // Now VertexCacheObject has been updated (in DataModify)
        }

        private void DropEdge(EdgeField edgeField)
        {
            RawRecord record = new RawRecord();
            record.Append(edgeField);
            DropEdgeOperator op = new DropEdgeOperator(this.dummyInputOp, this.Command, 0);
            op.DataModify(record);

            // Now VertexCacheObject has been updated (in DataModify)
        }

        private void DropVertexSingleProperty(VertexSinglePropertyField vp)
        {
            if (vp.PropertyName == this.Command.Connection.RealPartitionKey) {
                throw new GraphViewException("Drop the partition-by property is not supported");
            }

            // Update DocDB
            VertexField vertexField = vp.VertexProperty.Vertex;
            JObject vertexObject = vertexField.VertexJObject;

            Debug.Assert(vertexObject[vp.PropertyName] != null);

            JArray vertexProperty = (JArray)vertexObject[vp.PropertyName];
            vertexProperty
                .First(singleProperty => (string)singleProperty[DocumentDBKeywords.KW_PROPERTY_ID] == vp.PropertyId)
                .Remove();
            if (vertexProperty.Count == 0) {
               vertexObject.Property(vp.PropertyName).Remove();
            }

            if (this.Command.InLazyMode)
            {
                DeltaLogDropVertexSingleProperty log = new DeltaLogDropVertexSingleProperty(vp.PropertyName, vp.PropertyId);
                this.Command.VertexCache.AddOrUpdateVertexDelta(vertexField, log);
            }
            else
            {
                this.Command.Connection.ReplaceOrDeleteDocumentAsync(vertexField.VertexId, vertexObject, 
                    this.Command.Connection.GetDocumentPartition(vertexObject), this.Command).Wait();
            }

            // Update vertex field
            VertexPropertyField vertexPropertyField = vertexField.VertexProperties[vp.PropertyName];
            bool found = vertexPropertyField.Multiples.Remove(vp.PropertyId);
            Debug.Assert(found);
            if (!vertexPropertyField.Multiples.Any()) {
                vertexField.VertexProperties.Remove(vp.PropertyName);
            }

        }

        private void DropVertexPropertyMetaProperty(ValuePropertyField metaProperty)
        {
            Debug.Assert(metaProperty.Parent is VertexSinglePropertyField);
#if DEBUG
            VertexSinglePropertyField vsp = (VertexSinglePropertyField)metaProperty.Parent;
            VertexField vertex = vsp.VertexProperty.Vertex;
            if (!vertex.ViaGraphAPI) {
                Debug.Assert(vertex.VertexJObject[vsp.PropertyName] is JArray);
                ////throw new GraphViewException("BUG: Compatible vertices should not have meta properties.");
            }
#endif

            VertexSinglePropertyField vertexSingleProperty = (VertexSinglePropertyField)metaProperty.Parent;

            VertexField vertexField = vertexSingleProperty.VertexProperty.Vertex;
            JObject vertexObject = vertexField.VertexJObject;

            Debug.Assert(vertexObject[vertexSingleProperty.PropertyName] != null);

            JToken propertyJToken = ((JArray) vertexObject[vertexSingleProperty.PropertyName])
                .First(singleProperty => (string) singleProperty[KW_PROPERTY_ID] == vertexSingleProperty.PropertyId);

            JObject metaPropertyJObject = (JObject) propertyJToken?[KW_PROPERTY_META];

            if (metaPropertyJObject != null) {
                metaPropertyJObject.Property(metaProperty.PropertyName)?.Remove();
                if (metaPropertyJObject.Count == 0) {
                    ((JObject)propertyJToken).Remove(KW_PROPERTY_META);
                }
            }

            if (this.Command.InLazyMode)
            {
                DeltaLogDropVertexMetaProperty log = new DeltaLogDropVertexMetaProperty(metaProperty.PropertyName,
                    vertexSingleProperty.PropertyName, vertexSingleProperty.PropertyId);
                this.Command.VertexCache.AddOrUpdateVertexDelta(vertexField, log);
            }
            else
            {
                // Update DocDB
                this.Command.Connection.ReplaceOrDeleteDocumentAsync(vertexField.VertexId, vertexObject,
                    this.Command.Connection.GetDocumentPartition(vertexObject), this.Command).Wait();
            }

            // Update vertex field
            vertexSingleProperty.MetaProperties.Remove(metaProperty.PropertyName);
        }

        private void DropEdgeProperty(EdgePropertyField ep)
        {
            List<Tuple<WValueExpression, WValueExpression, int>> propertyList = new List<Tuple<WValueExpression, WValueExpression, int>>();
            propertyList.Add(
                new Tuple<WValueExpression, WValueExpression, int>(
                    new WValueExpression(ep.PropertyName, true), 
                    new WValueExpression("null", false), 
                    0));
            UpdateEdgePropertiesOperator op = new UpdateEdgePropertiesOperator(
                this.dummyInputOp, 
                this.Command,
                0, 
                propertyList
                );
            RawRecord record = new RawRecord();
            record.Append(ep.Edge);
            op.DataModify(record);

            // Now VertexCacheObject has been updated (in DataModify)
            Debug.Assert(!ep.Edge.EdgeProperties.ContainsKey(ep.PropertyName));
        }

        internal override RawRecord DataModify(RawRecord record)
        {
            FieldObject dropTarget = record[this.dropTargetIndex];

            VertexField vertexField = dropTarget as VertexField;;
            if (vertexField != null) {
                this.DropVertex(vertexField);
                return null;
            }

            EdgeField edgeField = dropTarget as EdgeField;
            if (edgeField != null)
            {
                this.DropEdge(edgeField);
                return null;
            }

            PropertyField property = dropTarget as PropertyField;
            if (property != null)
            {
                if (property is VertexSinglePropertyField)
                {
                    this.DropVertexSingleProperty((VertexSinglePropertyField)property);
                }
                else if (property is EdgePropertyField)
                {
                    this.DropEdgeProperty((EdgePropertyField)property);
                }
                else
                {
                    this.DropVertexPropertyMetaProperty((ValuePropertyField)property);
                }

                return null;
            }

            // Should not reach here
            throw new GraphViewException("The incoming object is not removable");
            return null;
        }
    }

    internal class UpdatePropertiesOperator : ModificationBaseOperator
    {
        private readonly int updateTargetIndex;
        private readonly List<WPropertyExpression> updateProperties;

        public UpdatePropertiesOperator(
            GraphViewExecutionOperator dummyInputOp,
            GraphViewCommand command,
            int updateTargetIndex,
            List<WPropertyExpression> updateProperties)
            : base(dummyInputOp, command)
        {
            this.updateTargetIndex = updateTargetIndex;
            this.updateProperties = updateProperties;
        }

        private void UpdatePropertiesOfVertex(VertexField vertex)
        {
            JObject vertexDocument = vertex.VertexJObject;

            foreach (WPropertyExpression property in this.updateProperties) {
                Debug.Assert(property.Value != null);
                
                string name = property.Key.Value;
                if (name == this.Command.Connection.RealPartitionKey) {
                    throw new GraphViewException("Updating the partition-by property is not supported.");
                }

                if (!vertex.ViaGraphAPI && vertexDocument[name] is JValue) {
                    // Add/Update an existing flat vertex property
                    throw new GraphViewException($"The adding/updating property '{name}' already exists as flat.");
                }

                // Construct single property
                JObject meta = new JObject();
                List<Tuple<string, string>> metaList = new List<Tuple<string, string>>();
                foreach (KeyValuePair<WValueExpression, WValueExpression> pair in property.MetaProperties) {
                    meta[pair.Key.Value] = pair.Value.ToJValue();
                    metaList.Add(new Tuple<string, string>(pair.Key.Value, pair.Value.Value));
                }
                string propertyId = GraphViewConnection.GenerateDocumentId();
                JObject singleProperty = new JObject {
                    [KW_PROPERTY_VALUE] = property.Value.ToJValue(),
                    [KW_PROPERTY_ID] = propertyId,
                };
                if (meta.Count > 0) {
                    singleProperty[KW_PROPERTY_META] = meta;
                }

                // Set / Append to multiProperty
                JArray multiProperty;
                if (vertexDocument[name] == null) {
                    multiProperty = new JArray();
                    vertexDocument[name] = multiProperty;
                }
                else {
                    multiProperty = (JArray)vertexDocument[name];
                }
                bool isMultiProperty = property.Cardinality != GremlinKeyword.PropertyCardinality.Single;
                if (!isMultiProperty) {
                    multiProperty.Clear();
                }
                multiProperty.Add(singleProperty);

                if (this.Command.InLazyMode)
                {
                    DeltaLogUpdateVertexSingleProperty log = new DeltaLogUpdateVertexSingleProperty(name,
                        property.Value.Value, propertyId, isMultiProperty, metaList);
                    this.Command.VertexCache.AddOrUpdateVertexDelta(vertex, log);
                }

                // Update vertex field
                VertexPropertyField vertexProperty;
                bool existed = vertex.VertexProperties.TryGetValue(name, out vertexProperty);
                if (!existed) {
                    vertexProperty = new VertexPropertyField(vertexDocument.Property(name), vertex);
                    vertex.VertexProperties.Add(name, vertexProperty);
                }
                else {
                    vertexProperty.Replace(vertexDocument.Property(name));
                }
            }

            if (!this.Command.InLazyMode)
            {
                // Upload to DB
                this.Command.Connection.ReplaceOrDeleteDocumentAsync(
                    vertex.VertexId, vertexDocument,
                    this.Command.Connection.GetDocumentPartition(vertexDocument), this.Command).Wait();
            }
            
        }

        private void UpdatePropertiesOfEdge(EdgeField edge)
        {
            List<Tuple<WValueExpression, WValueExpression, int>> propertyList =
                new List<Tuple<WValueExpression, WValueExpression, int>>();
            foreach (WPropertyExpression property in this.updateProperties) {
                if (property.Cardinality == GremlinKeyword.PropertyCardinality.List ||
                    property.MetaProperties.Count > 0) {
                    throw new Exception("Can't create meta property or duplicated property on edges");
                }

                propertyList.Add(new Tuple<WValueExpression, WValueExpression, int>(property.Key, property.Value, 0));
            }

            RawRecord record = new RawRecord();
            record.Append(edge);
            UpdateEdgePropertiesOperator op = new UpdateEdgePropertiesOperator(this.InputOperator, this.Command, 0, propertyList);
            op.DataModify(record);
        }

        private void UpdateMetaPropertiesOfSingleVertexProperty(VertexSinglePropertyField vp)
        {
            if (!vp.VertexProperty.Vertex.ViaGraphAPI) {
                // We know this property must be added via GraphAPI (if exist)
                JToken prop = vp.VertexProperty.Vertex.VertexJObject[vp.PropertyName];
                Debug.Assert(prop == null || prop is JObject);
            }

            string vertexId = vp.VertexProperty.Vertex.VertexId;
            JObject vertexDocument = vp.VertexProperty.Vertex.VertexJObject;
            JObject singleProperty = (JObject)((JArray)vertexDocument[vp.PropertyName])
                .First(single => (string) single[KW_PROPERTY_ID] == vp.PropertyId);
            JObject meta = (JObject)singleProperty[KW_PROPERTY_META];

            if (meta == null && this.updateProperties.Count > 0) {
                meta = new JObject();
                singleProperty[KW_PROPERTY_META] = meta;
            }
            List<Tuple<string, string>> metaList = new List<Tuple<string, string>>();
            foreach (WPropertyExpression property in this.updateProperties) {
                if (property.Cardinality == GremlinKeyword.PropertyCardinality.List ||
                    property.MetaProperties.Count > 0) {
                    throw new Exception("Can't create meta property or duplicated property on vertex-property's meta property");
                }

                meta[property.Key.Value] = property.Value.ToJValue();
                metaList.Add(new Tuple<string, string>(property.Key.Value, property.Value.Value));
            }

            // Update vertex single property
            vp.Replace(singleProperty);

            if (this.Command.InLazyMode)
            {
                DeltaLogUpdateVertexMetaPropertyOfSingleProperty log = new DeltaLogUpdateVertexMetaPropertyOfSingleProperty(
                    vp.PropertyName, vp.PropertyId, metaList);
                this.Command.VertexCache.AddOrUpdateVertexDelta(vp.VertexProperty.Vertex, log);
            }
            else
            {
                // Upload to DB
                this.Command.Connection.ReplaceOrDeleteDocumentAsync(vertexId, vertexDocument,
                    this.Command.Connection.GetDocumentPartition(vertexDocument), this.Command).Wait();
            }
            
        }

        internal override RawRecord DataModify(RawRecord record)
        {
            FieldObject updateTarget = record[this.updateTargetIndex];

            VertexField vertex = updateTarget as VertexField; ;
            if (vertex != null)
            {
                this.UpdatePropertiesOfVertex(vertex);

                return record;
            }

            EdgeField edge = updateTarget as EdgeField;
            if (edge != null)
            {
                this.UpdatePropertiesOfEdge(edge);

                return record;
            }

            PropertyField property = updateTarget as PropertyField;
            if (property != null)
            {
                if (property is VertexSinglePropertyField)
                {
                    this.UpdateMetaPropertiesOfSingleVertexProperty((VertexSinglePropertyField) property);
                }
                else
                {
                    throw new GraphViewException($"BUG: updateTarget is {nameof(PropertyField)}: {property.GetType()}");
                }

                return record;
            }

            // Should not reach here
            throw new Exception("BUG: Should not get here!");
        }

        public override RawRecord Next()
        {
            RawRecord srcRecord = null;

            while (InputOperator.State() && (srcRecord = InputOperator.Next()) != null)
            {
                RawRecord result = DataModify(srcRecord);
                if (result == null) continue;

                // Return the srcRecord
                return srcRecord;
            }

            Close();
            return null;
        }
    }

    internal class DropNodeOperator : ModificationBaseOperator
    {
        private int _nodeIdIndex;

        public DropNodeOperator(GraphViewExecutionOperator dummyInputOp, GraphViewCommand command, int pNodeIdIndex)
            : base(dummyInputOp, command)
        {
            _nodeIdIndex = pNodeIdIndex;
        }

        // TODO: Batch upload for the DropEdge part
        internal override RawRecord DataModify(RawRecord record)
        {
            string vertexId = record[this._nodeIdIndex].ToValue;

            // Temporarily change
            DropEdgeOperator dropEdgeOp = new DropEdgeOperator(null, this.Command, 0);
            RawRecord temp = new RawRecord(2);

            VertexField vertex = this.Command.VertexCache.GetVertexField(vertexId);

            foreach (EdgeField outEdge in vertex.AdjacencyList.AllEdges.ToList()) {
                temp.fieldValues[0] = outEdge;
                dropEdgeOp.DataModify(temp);
            }

            foreach (EdgeField inEdge in vertex.RevAdjacencyList.AllEdges.ToList()) {
                temp.fieldValues[0] = inEdge;
                dropEdgeOp.DataModify(temp);
            }

            // Delete the vertex-document!
            JObject vertexObject = vertex.VertexJObject;
#if DEBUG
            if (vertex.ViaGraphAPI) {
                Debug.Assert(vertexObject[KW_VERTEX_EDGE] is JArray);
                if (!EdgeDocumentHelper.IsSpilledVertex(vertexObject, false)) {
                    Debug.Assert(((JArray)vertexObject[KW_VERTEX_EDGE]).Count == 0);
                }

                if (this.Command.Connection.UseReverseEdges) {
                    Debug.Assert(vertexObject[KW_VERTEX_REV_EDGE] is JArray);
                    if (!EdgeDocumentHelper.IsSpilledVertex(vertexObject, true)) {
                        Debug.Assert(((JArray)vertexObject[KW_VERTEX_REV_EDGE]).Count == 0);
                    }
                }
            }
#endif
            if (this.Command.InLazyMode)
            {
                DeltaLogDropVertex log = new DeltaLogDropVertex();
                this.Command.VertexCache.AddOrUpdateVertexDelta(vertex, log);
            }
            else
            {
                this.Command.Connection.ReplaceOrDeleteDocumentAsync(vertexId, null,
                    this.Command.Connection.GetDocumentPartition(vertexObject), this.Command).Wait();
            }

            // Update VertexCache
            this.Command.VertexCache.TryRemoveVertexField(vertexId);

            return null;
        }
    }

    internal class AddEOperator : ModificationBaseOperator
    {
        //
        // if otherVTag == 0, this newly added edge's otherV() is the src vertex.
        // Otherwise, it's the sink vertex
        //
        private int otherVTag;
        //
        // The subquery operator select the vertex ID of source and sink of the edge to be added or deleted
        //
        private ConstantSourceOperator srcSubQuerySourceOp;
        private ConstantSourceOperator sinkSubQuerySouceOp;
        private GraphViewExecutionOperator srcSubQueryOp;
        private GraphViewExecutionOperator sinkSubQueryOp;
        //
        // The initial json object string of to-be-inserted edge, waiting to update the edgeOffset field
        //
        private JObject edgeJsonObject;
        private List<string> edgeProperties;

        public AddEOperator(GraphViewExecutionOperator inputOp, GraphViewCommand command,
            ConstantSourceOperator srcSubQuerySourceOp, GraphViewExecutionOperator srcSubQueryOp,
            ConstantSourceOperator sinkSubQuerySouceOp, GraphViewExecutionOperator sinkSubQueryOp,
            int otherVTag, JObject edgeJsonObject, List<string> projectedFieldList)
            : base(inputOp, command)
        {
            this.srcSubQuerySourceOp = srcSubQuerySourceOp;
            this.sinkSubQuerySouceOp = sinkSubQuerySouceOp;
            this.srcSubQueryOp = srcSubQueryOp;
            this.sinkSubQueryOp = sinkSubQueryOp;
            this.otherVTag = otherVTag;
            this.edgeJsonObject = edgeJsonObject;
            this.edgeProperties = projectedFieldList;
        }

        internal override RawRecord DataModify(RawRecord record)
        {
            srcSubQuerySourceOp.ConstantSource = record;
            srcSubQueryOp.ResetState();
            sinkSubQuerySouceOp.ConstantSource = record;
            sinkSubQuerySouceOp.ResetState();
            //
            // Gremlin will only add edge from the first vertex generated by the src subquery 
            // to the first vertex generated by the sink subquery
            //
            RawRecord srcRecord = srcSubQueryOp.Next();
            RawRecord sinkRecord = sinkSubQueryOp.Next();

            VertexField srcVertexField = srcRecord[0] as VertexField;
            VertexField sinkVertexField = sinkRecord[0] as VertexField;

            if (srcVertexField == null || sinkVertexField == null) return null;

            string srcId = srcVertexField[KW_DOC_ID].ToValue;
            string sinkId = sinkVertexField[KW_DOC_ID].ToValue;

            JObject srcVertexObject = srcVertexField.VertexJObject;
            JObject sinkVertexObject = sinkVertexField.VertexJObject;
            if (srcId.Equals(sinkId)) {
                Debug.Assert(ReferenceEquals(sinkVertexObject, srcVertexObject));
                Debug.Assert(ReferenceEquals(sinkVertexField, srcVertexField));
            }


            //
            // Interact with DocDB and add the edge
            // - For a small-degree vertex (now filled into one document), insert the edge in-place
            //     - If the upload succeeds, done!
            //     - If the upload fails with size-limit-exceeded(SLE), put either incoming or outgoing edges into a seperate document
            // - For a large-degree vertex (already spilled)
            //     - Update either incoming or outgoing edges in the seperate edge-document
            //     - If the upload fails with SLE, create a new document to store the edge, and update the vertex document
            //
            JObject outEdgeObject, inEdgeObject;
            string outEdgeDocID = null, inEdgeDocID = null;

            outEdgeObject = (JObject)this.edgeJsonObject.DeepClone();
            inEdgeObject = (JObject)this.edgeJsonObject.DeepClone();

            // Add "id" property to edgeObject
            string edgeId = GraphViewConnection.GenerateDocumentId();

            string srcLabel = srcVertexObject[KW_VERTEX_LABEL]?.ToString();
            string sinkLabel = sinkVertexObject[KW_VERTEX_LABEL]?.ToString();
            GraphViewJsonCommand.UpdateEdgeMetaProperty(outEdgeObject, edgeId, false, sinkId, sinkLabel, sinkVertexField.Partition);
            GraphViewJsonCommand.UpdateEdgeMetaProperty(inEdgeObject, edgeId, true, srcId, srcLabel, srcVertexField.Partition);

            if (!this.Command.InLazyMode)
            {
                EdgeDocumentHelper.InsertEdgeObjectInternal(this.Command, srcVertexObject, srcVertexField, outEdgeObject, false, out outEdgeDocID); // srcVertex uploaded

                if (this.Command.Connection.UseReverseEdges)
                {
                    EdgeDocumentHelper.InsertEdgeObjectInternal(this.Command, sinkVertexObject, sinkVertexField, inEdgeObject, true, out inEdgeDocID); // sinkVertex uploaded
                }
                else
                {
                    inEdgeDocID = EdgeDocumentHelper.VirtualReverseEdgeDocId;
                }
            }

            // Update vertex's adjacency list and reverse adjacency list (in vertex field)
            EdgeField outEdgeField = srcVertexField.AdjacencyList.TryAddEdgeField(
                (string)outEdgeObject[KW_EDGE_ID],
                () => EdgeField.ConstructForwardEdgeField(srcId, srcVertexField.VertexLabel, srcVertexField.Partition, outEdgeDocID, outEdgeObject));

            EdgeField inEdgeField = sinkVertexField.RevAdjacencyList.TryAddEdgeField(
                (string)inEdgeObject[KW_EDGE_ID], 
                () => EdgeField.ConstructBackwardEdgeField(sinkId, sinkVertexField.VertexLabel, sinkVertexField.Partition, inEdgeDocID, inEdgeObject));

            if (this.Command.InLazyMode)
            {
                DeltaLogAddEdge log = new DeltaLogAddEdge();
                this.Command.VertexCache.AddOrUpdateEdgeDelta(outEdgeField, srcVertexField, inEdgeField, sinkVertexField, log, this.Command.Connection.UseReverseEdges);
            }

            // Construct the newly added edge's RawRecord
            RawRecord result = new RawRecord();

            // source, sink, other, edgeId, *
            result.Append(new StringField(srcId));
            result.Append(new StringField(sinkId));
            result.Append(new StringField(otherVTag == 0 ? srcId : sinkId));
            result.Append(new StringField(outEdgeField.EdgeId));
            result.Append(outEdgeField);

            for (int i = GraphViewReservedProperties.ReservedEdgeProperties.Count; i < edgeProperties.Count; i++) {
                FieldObject fieldValue = outEdgeField[edgeProperties[i]];
                result.Append(fieldValue);
            }

            return result;
        }
    }

    internal class DropEdgeOperator : ModificationBaseOperator
    {
        private readonly int edgeFieldIndex;

        public DropEdgeOperator(GraphViewExecutionOperator inputOp, GraphViewCommand command, int edgeFieldIndex)
            : base(inputOp, command)
        {
            this.edgeFieldIndex = edgeFieldIndex;
        }

        internal override RawRecord DataModify(RawRecord record)
        {
            EdgeField edgeField = (EdgeField)record[this.edgeFieldIndex];
            string edgeId = edgeField.EdgeId;
            string srcId = edgeField.OutV;
            string sinkId = edgeField.InV;
            string srcVertexPartition = edgeField.OutVPartition;
            string sinkVertexPartition = edgeField.InVPartition;

            VertexField srcVertexField = this.Command.VertexCache.GetVertexField(srcId, srcVertexPartition);
            VertexField sinkVertexField = this.Command.VertexCache.GetVertexField(sinkId, sinkVertexPartition);

            if (this.Command.InLazyMode)
            {
                DeltaLogDropEdge log = new DeltaLogDropEdge();
                this.Command.VertexCache.AddOrUpdateEdgeDelta(edgeField, srcVertexField, 
                    null, sinkVertexField, log, this.Command.Connection.UseReverseEdges);
            }
            else
            {
                JObject srcVertexObject = srcVertexField.VertexJObject;
                JObject srcEdgeObject;
                string srcEdgeDocId;

                EdgeDocumentHelper.FindEdgeBySourceAndEdgeId(
                    this.Command, srcVertexObject, srcId, edgeId, false,
                    out srcEdgeObject, out srcEdgeDocId);

                if (srcEdgeObject == null)
                {
                    //TODO: Check is this condition alright?
                    return null;
                }

                JObject sinkVertexObject = sinkVertexField.VertexJObject;
                string sinkEdgeDocId = null;

                if (this.Command.Connection.UseReverseEdges)
                {
                    if (!string.Equals(sinkId, srcId))
                    {
                        JObject dummySinkEdgeObject;
                        EdgeDocumentHelper.FindEdgeBySourceAndEdgeId(
                            this.Command, sinkVertexObject, srcId, edgeId, true,
                            out dummySinkEdgeObject, out sinkEdgeDocId);
                    }
                    else
                    {
                        Debug.Assert(object.ReferenceEquals(sinkVertexField, srcVertexField));
                        Debug.Assert(sinkVertexObject == srcVertexObject);
                        sinkEdgeDocId = srcEdgeDocId;
                    }
                }

                // <docId, <docJson, partition>>
                Dictionary<string, Tuple<JObject, string>> uploadDocuments = new Dictionary<string, Tuple<JObject, string>>();
                EdgeDocumentHelper.RemoveEdge(uploadDocuments, this.Command, srcEdgeDocId,
                    srcVertexField, false, srcId, edgeId);
                if (this.Command.Connection.UseReverseEdges)
                {
                    EdgeDocumentHelper.RemoveEdge(uploadDocuments, this.Command, sinkEdgeDocId,
                        sinkVertexField, true, srcId, edgeId);
                }
                this.Command.Connection.ReplaceOrDeleteDocumentsAsync(uploadDocuments, this.Command).Wait();

#if DEBUG
                // NOTE: srcVertexObject is excatly the reference of srcVertexField.VertexJObject
                // NOTE: sinkVertexObject is excatly the reference of sinkVertexField.VertexJObject

                // If source vertex is not spilled, the outgoing edge JArray of srcVertexField.VertexJObject should have been updated
                if (!EdgeDocumentHelper.IsSpilledVertex(srcVertexField.VertexJObject, false))
                {
                    Debug.Assert(
                        srcVertexField.VertexJObject[KW_VERTEX_EDGE].Cast<JObject>().All(
                            edgeObj => (string)edgeObj[KW_EDGE_ID] != edgeId));
                }

                if (this.Command.Connection.UseReverseEdges)
                {
                    // If sink vertex is not spilled, the incoming edge JArray of sinkVertexField.VertexJObject should have been updated
                    if (!EdgeDocumentHelper.IsSpilledVertex(srcVertexField.VertexJObject, true))
                    {
                        Debug.Assert(
                            sinkVertexField.VertexJObject[KW_VERTEX_REV_EDGE].Cast<JObject>().All(
                                edgeObj => (string)edgeObj[KW_EDGE_ID] != edgeId));
                    }
                }
#endif
            }

            srcVertexField.AdjacencyList.RemoveEdgeField(edgeId);
            sinkVertexField.RevAdjacencyList.RemoveEdgeField(edgeId);

            return null;
        }
    }

    internal abstract class UpdatePropertiesBaseOperator : ModificationBaseOperator
    {
        internal enum UpdatePropertyMode
        {
            Set,
            Append
        };

        protected UpdatePropertyMode Mode;
        /// <summary>
        /// Item1 is property key.
        /// Item2 is property value. If it is null, then delete the property
        /// Item3 is property's index in the input record. If it is -1, then the input record doesn't contain this property.
        /// </summary>
        // TODO: Now the item3 is useless
        protected List<Tuple<WValueExpression, WValueExpression, int>> PropertiesToBeUpdated;

        protected UpdatePropertiesBaseOperator(GraphViewExecutionOperator inputOp, GraphViewCommand command,
            List<Tuple<WValueExpression, WValueExpression, int>> pPropertiesToBeUpdated, UpdatePropertyMode pMode = UpdatePropertyMode.Set)
            : base(inputOp, command)
        {
            PropertiesToBeUpdated = pPropertiesToBeUpdated;
            Mode = pMode;
        }

        public override RawRecord Next()
        {
            RawRecord srcRecord = null;

            while (InputOperator.State() && (srcRecord = InputOperator.Next()) != null)
            {
                RawRecord result = DataModify(srcRecord);
                if (result == null) continue;

                return srcRecord;
            }

            Close();
            return null;
        }
    }

    internal class UpdateEdgePropertiesOperator : UpdatePropertiesBaseOperator
    {
        private readonly int edgeFieldIndex;

        public UpdateEdgePropertiesOperator(
            GraphViewExecutionOperator inputOp, GraphViewCommand command,
            int edgeFieldIndex,
            List<Tuple<WValueExpression, WValueExpression, int>> propertiesList,
            UpdatePropertyMode pMode = UpdatePropertyMode.Set)
            : base(inputOp, command, propertiesList, pMode)
        {
            this.edgeFieldIndex = edgeFieldIndex;
        }

        internal override RawRecord DataModify(RawRecord record)
        {
            EdgeField edgeField = (EdgeField) record[this.edgeFieldIndex];
            string edgeId = edgeField.EdgeId;

            string srcVertexId = edgeField.OutV;
            string srcVertexPartition = edgeField.OutVPartition;
            VertexField srcVertexField = this.Command.VertexCache.GetVertexField(srcVertexId, srcVertexPartition);
            JObject srcVertexObject = srcVertexField.VertexJObject;

            VertexField sinkVertexField;
            JObject sinkVertexObject;
            string sinkVertexId = edgeField.InV;
            string sinkVertexPartition = edgeField.InVPartition;

            bool foundSink;
            if (this.Command.Connection.UseReverseEdges)
            {
                sinkVertexField = this.Command.VertexCache.GetVertexField(sinkVertexId, sinkVertexPartition);
                sinkVertexObject = sinkVertexField.VertexJObject;
                foundSink = true;
            }
            else
            {
                foundSink = this.Command.VertexCache.TryGetVertexField(sinkVertexId, out sinkVertexField);
                sinkVertexObject = sinkVertexField?.VertexJObject;
            }

            EdgeField outEdgeField = srcVertexField.AdjacencyList.GetEdgeField(edgeId, true);
            EdgeField inEdgeField = null;
            if (this.Command.Connection.UseReverseEdges)
            {
                inEdgeField = sinkVertexField?.RevAdjacencyList.GetEdgeField(edgeId, true);
            }

            JObject outEdgeObject = outEdgeField.EdgeJObject;
            string outEdgeDocId = null;

            JObject inEdgeObject = inEdgeField?.EdgeJObject;
            string inEdgeDocId = null;

            if (!this.Command.InLazyMode)
            {
                EdgeDocumentHelper.FindEdgeBySourceAndEdgeId(
                    this.Command, srcVertexObject, srcVertexId, edgeId, false,
                    out outEdgeObject, out outEdgeDocId);

                if (outEdgeObject == null)
                {
                    Debug.WriteLine(
                        $"[UpdateEdgePropertiesOperator] The edge does not exist: vertexId = {srcVertexId}, edgeId = {edgeId}");
                    return null;
                }

                if (this.Command.Connection.UseReverseEdges)
                {
                    Debug.Assert(foundSink);

                    if (sinkVertexId.Equals(srcVertexId))
                    {
                        Debug.Assert(object.ReferenceEquals(sinkVertexField, srcVertexField));
                        Debug.Assert(object.ReferenceEquals(sinkVertexObject, srcVertexObject));
                        inEdgeDocId = outEdgeDocId;
                    }
                    else
                    {
                        EdgeDocumentHelper.FindEdgeBySourceAndEdgeId(
                            this.Command, sinkVertexObject, srcVertexId, edgeId, true,
                            out inEdgeObject, out inEdgeDocId);
                    }
                }
            }

            List<Tuple<string, EdgeDeltaType, string>> deltaProperties = new List<Tuple<string, EdgeDeltaType, string>>();
            List<Tuple<string, EdgeDeltaType, string>> RevDeltaProperties = null;
            if (this.Command.Connection.UseReverseEdges)
            {
                RevDeltaProperties = new List<Tuple<string, EdgeDeltaType, string>>();
            }

            // Drop all non-reserved properties
            if (this.PropertiesToBeUpdated.Count == 1 &&
                !this.PropertiesToBeUpdated[0].Item1.SingleQuoted &&
                this.PropertiesToBeUpdated[0].Item1.Value.Equals("*", StringComparison.OrdinalIgnoreCase) &&
                !this.PropertiesToBeUpdated[0].Item2.SingleQuoted &&
                this.PropertiesToBeUpdated[0].Item2.Value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("BUG: This condition is obsolete. Code should not reach here now!");
            }
            else
            {
                foreach (Tuple<WValueExpression, WValueExpression, int> tuple in this.PropertiesToBeUpdated)
                {
                    WValueExpression keyExpression = tuple.Item1;
                    WValueExpression valueExpression = tuple.Item2;

                    if (this.Mode == UpdatePropertyMode.Set)
                    {
                        // Modify edgeObject (update the edge property)
                        JProperty updatedProperty = GraphViewJsonCommand.UpdateProperty(
                            outEdgeObject, keyExpression, valueExpression);
                        // Update VertexCache
                        if (updatedProperty == null)
                        {
                            outEdgeField.EdgeProperties.Remove(keyExpression.Value);
                            if (this.Command.InLazyMode)
                            {
                                deltaProperties.Add(new Tuple<string, EdgeDeltaType, string>(
                                    keyExpression.Value, EdgeDeltaType.DropProperty, null));
                            }
                        }
                        else
                        {
                            outEdgeField.UpdateEdgeProperty(updatedProperty);
                            if (this.Command.InLazyMode)
                            {
                                deltaProperties.Add(new Tuple<string, EdgeDeltaType, string>(
                                    keyExpression.Value, EdgeDeltaType.UpdateProperty, valueExpression.Value));
                            }
                        }

                        if (this.Command.Connection.UseReverseEdges && inEdgeField != null)
                        {
                            // Modify edgeObject (update the edge property)
                            updatedProperty = GraphViewJsonCommand.UpdateProperty(
                                inEdgeObject, keyExpression, valueExpression);
                        }

                        // Update VertexCache (if found)
                        if (inEdgeField != null)
                        {
                            if (updatedProperty == null)
                            {
                                inEdgeField.EdgeProperties.Remove(keyExpression.Value);
                                if (this.Command.InLazyMode)
                                {
                                    RevDeltaProperties.Add(new Tuple<string, EdgeDeltaType, string>(
                                        keyExpression.Value, EdgeDeltaType.DropProperty, null));
                                }
                            }
                            else
                            {
                                inEdgeField.UpdateEdgeProperty(updatedProperty);
                                RevDeltaProperties.Add(new Tuple<string, EdgeDeltaType, string>(
                                    keyExpression.Value, EdgeDeltaType.UpdateProperty, valueExpression.Value));
                            }
                        }
                    }
                    else
                    {
                        throw new GraphViewException("Edges can't have duplicated-name properties.");
                    }
                }
            }

            if (this.Command.InLazyMode)
            {
                DeltaLogUpdateEdgeProperty log = new DeltaLogUpdateEdgeProperty(deltaProperties, RevDeltaProperties);
                this.Command.VertexCache.AddOrUpdateEdgeDelta(outEdgeField, srcVertexField, 
                    inEdgeField, sinkVertexField, log, this.Command.Connection.UseReverseEdges);
            }
            else
            {
                // Interact with DocDB to update the property 
                EdgeDocumentHelper.UpdateEdgeProperty(this.Command, srcVertexObject, outEdgeDocId, false,
                    outEdgeObject);
                if (this.Command.Connection.UseReverseEdges)
                {
                    EdgeDocumentHelper.UpdateEdgeProperty(this.Command, sinkVertexObject, inEdgeDocId, true,
                        inEdgeObject);
                }
            }

            // Drop edge property
            if (this.PropertiesToBeUpdated.Any(t => t.Item2 == null)) return null;
            return record;
        }

    }

    internal class CommitOperator : GraphViewExecutionOperator
    {
        private GraphViewCommand Command;
        private GraphViewExecutionOperator InputOp;
        private Queue<RawRecord> OutputBuffer;

        public CommitOperator(GraphViewCommand command, GraphViewExecutionOperator inputOp)
        {
            this.Command = command;
            this.Command.InLazyMode = true;
            this.InputOp = inputOp;
            this.OutputBuffer = new Queue<RawRecord>();

            this.Open();
        }

        public override RawRecord Next()
        {
            RawRecord r = null;
            while (InputOp.State() && (r = InputOp.Next()) != null)
            {
                OutputBuffer.Enqueue(new RawRecord(r));
            }

            Command.VertexCache.UploadDelta();

            if (OutputBuffer.Count <= 1) Close();
            if (OutputBuffer.Count != 0) return OutputBuffer.Dequeue();
            return null;
        }

        public override void ResetState()
        {
            this.InputOp.ResetState();
            this.Open();
        }
    }
}
