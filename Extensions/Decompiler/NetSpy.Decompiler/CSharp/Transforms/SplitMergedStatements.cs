// Copyright (c) 2026 NetSpy contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;

using NetSpy.Decompiler.CSharp.Resolver;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.IL;
using NetSpy.Decompiler.IL.Transforms;
using NetSpy.Decompiler.TypeSystem;
using NetSpy.Decompiler.Util;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "var result = Compute(); return result;" compiles (optimized) to a value left on the stack, so the
	/// local is gone and the decompiler writes "return Compute();". The PDB still has two sequence points
	/// (two source statements): the part of the statement evaluated before the second one becomes a local again.
	/// </summary>
	public sealed class SplitMergedStatements : IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.SourceLayoutFromPdb || context.DecompileRun.DebugInfoProvider is not { } provider)
				return;
			var mainModule = context.TypeSystem.MainModule;
			foreach (var entity in rootNode.DescendantsAndSelf.OfType<EntityDeclaration>().ToList())
			{
				var body = entity switch {
					MethodDeclaration m => m.Body,
					ConstructorDeclaration c => c.Body,
					OperatorDeclaration o => o.Body,
					Accessor a => a.Body,
					_ => null
				};
				var function = entity.Annotation<ILFunction>();
				if (body is null || function is null || entity.GetSymbol() is not IMethod declaredMethod)
					continue;
				var method = function.MoveNextMethod ?? declaredMethod;
				if (method.ParentModule != mainModule || method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition)
					continue;
				var points = provider.GetSequencePoints((MethodDefinitionHandle)method.MetadataToken)
					.Where(sp => !sp.IsHidden && !ApplySourceLayout.IsBrace(sp)).OrderBy(sp => sp.Offset).ToList();
				if (points.Count < 2)
					continue;
				var usedNames = new HashSet<string>(body.Descendants.OfType<Identifier>().Select(i => i.Name));
				foreach (var statement in body.Descendants.OfType<Statement>().Where(s => s.Parent is BlockStatement).ToList())
				{
					if (IsInOtherFunction(statement, body))
						continue;
					TrySplit(statement, points, function, usedNames, context);
				}
			}
		}

		void TrySplit(Statement statement, List<DebugInfo.SequencePoint> points, ILFunction function, HashSet<string> usedNames, TransformContext context)
		{
			if (statement is not (ExpressionStatement or ReturnStatement or ThrowStatement or YieldReturnStatement
				or VariableDeclarationStatement { Variables.Count: 1 }))
				return;
			var ranges = Ranges(statement).ToList();
			if (ranges.Count == 0)
				return;
			var covering = points.Where(sp => ranges.Any(r => r.Start < sp.EndOffset && sp.Offset <= r.InclusiveEnd)).ToList();
			if (covering.Count != 2 || covering[0].StartLine >= covering[1].StartLine)
				return;
			int split = covering[1].Offset;
			// the first subexpression (pre-order) whose IL is all before the second statement
			var candidate = statement.Descendants.OfType<Expression>().FirstOrDefault(e => Ranges(e).Any() && Ranges(e).All(r => r.InclusiveEnd < split));
			if (candidate is null || !CanBeLocal(candidate))
				return;
			// everything else (except the operations consuming it) is evaluated after it
			var ancestors = new HashSet<AstNode>(candidate.Ancestors);
			var inside = new HashSet<AstNode>(candidate.DescendantsAndSelf);
			foreach (var node in statement.DescendantsAndSelf)
			{
				if (inside.Contains(node) || ancestors.Contains(node))
					continue;
				if (OwnRanges(node).Any(r => r.Start < split))
					return;
			}
			if (candidate.GetResolveResult() is not { Type: var type } || type.Kind is TypeKind.Unknown or TypeKind.Void or TypeKind.Null)
				return;

			var valueContext = candidate.Annotation<ILInstruction>();
			string name = AssignVariableNames.GenerateVariableName(function, type, context.DecompileRun.UsingScope, valueContext);
			string baseName = name;
			for (int i = 2; usedNames.Contains(name); i++)
				name = baseName + i;
			usedNames.Add(name);
			var reference = new IdentifierExpression(name);
			candidate.ReplaceWith(reference);
			// "(object)42" made the conversion the declaration did: "object o = 42;"
			if (candidate is CastExpression { Expression: { } converted } && converted.GetResolveResult() is { Type: { Kind: not TypeKind.Unknown } convertedType }
				&& CSharpConversions.Get(context.TypeSystem) is var conversions
				&& conversions.ImplicitConversion(convertedType, type) is { IsValid: true, IsUserDefined: false })
				candidate = converted.Detach();
			AstType declaredType = context.Settings.ImplicitlyTypedLocals && IsTypeApparent(candidate) || type.ContainsAnonymousType()
				? new SimpleType("var")
				: context.TypeSystemAstBuilder.ConvertType(type);
			var declaration = new VariableDeclarationStatement(declaredType, name, candidate);
			statement.Parent!.InsertChildBefore(statement, declaration, Slots.Statement);
		}

		static bool CanBeLocal(Expression expression)
		{
			if (expression is LambdaExpression or AnonymousMethodExpression or DirectionExpression or NamedArgumentExpression
				or NamedExpression or ThisReferenceExpression or BaseReferenceExpression or TypeReferenceExpression
				or NullReferenceExpression or ArrayInitializerExpression)
				return false;
			// not something assigned to
			if (expression.Parent is AssignmentExpression assignment && assignment.Left == expression)
				return false;
			if (expression.Parent is UnaryOperatorExpression { Operator: UnaryOperatorType.Increment or UnaryOperatorType.Decrement
				or UnaryOperatorType.PostIncrement or UnaryOperatorType.PostDecrement or UnaryOperatorType.AddressOf })
				return false;
			return !expression.Ancestors.Any(a => a is LambdaExpression or AnonymousMethodExpression or QueryExpression);
		}

		static bool IsTypeApparent(Expression initializer)
		{
			return initializer is ObjectCreateExpression { Type: not null } or ArrayCreateExpression { Type: not null }
				or CastExpression or AsExpression or DefaultValueExpression or AnonymousTypeCreateExpression;
		}

		static IEnumerable<Interval> Ranges(AstNode node) => node.DescendantsAndSelf.SelectMany(OwnRanges);

		static IEnumerable<Interval> OwnRanges(AstNode node) => node.Annotations.OfType<ILInstruction>().SelectMany(i => i.ILRanges).Where(r => !r.IsEmpty);

		static bool IsInOtherFunction(AstNode node, AstNode body)
		{
			for (var n = node.Parent; n != null && n != body; n = n.Parent)
			{
				if (n is LambdaExpression or AnonymousMethodExpression or LocalFunctionDeclarationStatement)
					return true;
			}
			return false;
		}
	}
}
