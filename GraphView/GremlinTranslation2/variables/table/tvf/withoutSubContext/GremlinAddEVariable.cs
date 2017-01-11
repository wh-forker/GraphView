﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView
{
    internal class GremlinAddEVariable: GremlinTableVariable
    {
        public GremlinVariable InputVariable { get; set; }
        public GremlinToSqlContext FromVertexContext { get; set; }
        public GremlinToSqlContext ToVertexContext { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public string EdgeLabel { get; set; }

        public GremlinAddEVariable(GremlinVariable inputVariable, string edgeLabel)
        {
            Properties = new Dictionary<string, object>();
            EdgeLabel = edgeLabel;
            InputVariable = inputVariable;
        }

        public override WTableReference ToTableReference()
        {
            List<WScalarExpression> parameters = new List<WScalarExpression>();
            parameters.Add(SqlUtil.GetScalarSubquery(GetSelectQueryBlock(FromVertexContext)));
            parameters.Add(SqlUtil.GetScalarSubquery(GetSelectQueryBlock(ToVertexContext)));
            if (EdgeLabel != null)
            {
                parameters.Add(SqlUtil.GetValueExpr(GremlinKeyword.Label));
                parameters.Add(SqlUtil.GetValueExpr(EdgeLabel));
            }
            foreach (var property in Properties)
            {
                parameters.Add(SqlUtil.GetValueExpr(property.Key));
                parameters.Add(SqlUtil.GetValueExpr(property.Value));
            }
            var secondTableRef = SqlUtil.GetFunctionTableReference(GremlinKeyword.func.AddE, parameters, this, VariableName);

            return SqlUtil.GetCrossApplyTableReference(null, secondTableRef);
        }

        private WSelectQueryBlock GetSelectQueryBlock(GremlinToSqlContext context)
        {
            if (context == null)
            {
                return SqlUtil.GetSimpleSelectQueryBlock(InputVariable.VariableName, new List<string>() { GremlinKeyword.NodeID }); ;
            }
            else
            {
                return context.ToSelectQueryBlock();
            } 
        }


        internal override void From(GremlinToSqlContext currentContext, string label)
        {
            throw new NotImplementedException();
        }

        internal override void From(GremlinToSqlContext currentContext, GremlinToSqlContext fromVertexContext)
        {
            FromVertexContext = fromVertexContext;
        }

        internal override void Property(GremlinToSqlContext currentContext, Dictionary<string, object> properties)
        {
            foreach (var pair in properties)
            {
                Properties[pair.Key] = pair.Value;
            }
        }

        internal override void To(GremlinToSqlContext currentContext, string label)
        {
            throw new NotImplementedException();
        }

        internal override void To(GremlinToSqlContext currentContext, GremlinToSqlContext toVertexContext)
        {
            ToVertexContext = toVertexContext;
        }
    }
}
