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

using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: "customers.Where((Customer c) => c.Age > 18)" → "customers.Where(c => c.Age > 18)".
	/// The parameter types are dropped only where the lambda's delegate type comes from its context
	/// without them: a typed variable/field, an assignment, a return, a cast, or a method argument whose
	/// overloads agree on the lambda's parameter types and whose type arguments don't depend on them.
	/// There, a void lambda of one statement also becomes "x => statement-expression".
	/// </summary>
	public sealed class ImplicitlyTypedLambdas : DepthFirstAstVisitor, IAstTransform
	{
		TransformContext context = null!;

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.ImplicitlyTypedLambdas)
				return;
			this.context = context;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitLambdaExpression(LambdaExpression lambda)
		{
			base.VisitLambdaExpression(lambda);
			var target = GetTargetKind(lambda);
			if (target == TargetKind.None)
				return;
			// "x => count++": only where no Func overload could take the value instead
			if (target == TargetKind.Exact && lambda.Body is BlockStatement { Statements.Count: 1 } block
				&& block.Statements.Single() is ExpressionStatement { Expression: AssignmentExpression or UnaryOperatorExpression {
					Operator: UnaryOperatorType.Increment or UnaryOperatorType.Decrement or UnaryOperatorType.PostIncrement or UnaryOperatorType.PostDecrement
				} or InvocationExpression or ObjectCreateExpression } statement)
			{
				lambda.Body = statement.Expression.Detach();
			}
			if (lambda.Parameters.Count == 0 || lambda.Parameters.Any(p => p.Type is null))
				return;
			if (lambda.Attributes.Any() || lambda.Parameters.Any(p => p.Attributes.Any() || p.IsParams || p.DefaultExpression is not null))
				return; // attributes, params and default values need the explicit list
			// "((int Id, string Name) t) => t.Name": the element names may exist only in the lambda's list
			if (lambda.Parameters.Any(p => p.Type!.DescendantsAndSelf.OfType<TupleTypeElement>().Any(e => !string.IsNullOrEmpty(e.Name))))
				return;
			if (lambda.Parameters.Any(p => p.ParameterModifier != ReferenceKind.None) && !context.Settings.ExtensionMembers)
				return; // "(x, out y) =>" is C# 14 (ExtensionMembers: a setting that is on from C# 14)
			foreach (var parameter in lambda.Parameters)
				parameter.Type = null;
		}

		enum TargetKind
		{
			/// <summary>The parameter types aren't known without the lambda's list.</summary>
			None,
			/// <summary>Known, but other overloads with the same parameter types exist (e.g. Func and Action).</summary>
			ParameterTypes,
			/// <summary>The delegate type is known.</summary>
			Exact
		}

		TargetKind GetTargetKind(LambdaExpression lambda)
		{
			AstNode node = lambda;
			if (node.Parent is CastExpression)
				return TargetKind.Exact;
			switch (node.Parent)
			{
				case VariableInitializer { Parent: VariableDeclarationStatement declaration }:
					return declaration.Type.IsVar() ? TargetKind.None : TargetKind.Exact;
				case VariableInitializer { Parent: FieldDeclaration or EventDeclaration }:
				case AssignmentExpression assignment when assignment.Right == node:
				case ReturnStatement:
				case LambdaExpression outer when outer.Body == node:
					return TargetKind.Exact;
				case InvocationExpression invocation when invocation.Target != node:
					return GetArgumentTarget(invocation, invocation.Arguments.ToList(), node);
				case ObjectCreateExpression creation:
					return GetArgumentTarget(creation, creation.Arguments.ToList(), node);
				case NamedArgumentExpression named when named.Parent is InvocationExpression or ObjectCreateExpression:
					var call = named.Parent;
					var arguments = call is InvocationExpression i ? i.Arguments.ToList() : ((ObjectCreateExpression)call).Arguments.ToList();
					return GetArgumentTarget(call, arguments, named);
				default:
					return TargetKind.None;
			}
		}

		TargetKind GetArgumentTarget(AstNode call, List<Expression> arguments, AstNode argument)
		{
			if (call.GetSymbol() is not IMethod method)
				return TargetKind.None;
			var definition = (IMethod)method.MemberDefinition;
			int index;
			if (argument is NamedArgumentExpression named)
			{
				index = definition.Parameters.ToList().FindIndex(p => p.Name == named.Name);
			}
			else
			{
				index = arguments.IndexOf((Expression)argument);
				if (call.GetResolveResult() is CSharpInvocationResolveResult { IsExtensionMethodInvocation: true })
					index++;
			}
			if (index < 0 || index >= definition.Parameters.Count || definition.Parameters[index].IsParams)
				return TargetKind.None;
			var invoke = definition.Parameters[index].Type.GetDelegateInvokeMethod();
			if (invoke == null)
				return TargetKind.None;
			// type arguments inferred only from the lambda's parameter types need them
			bool explicitTypeArguments = call is InvocationExpression { Target: MemberReferenceExpression { TypeArguments.Count: > 0 } or IdentifierExpression { TypeArguments.Count: > 0 } };
			if (definition.TypeParameters.Count > 0 && !explicitTypeArguments)
			{
				var fromOthers = new HashSet<ITypeParameter>(definition.Parameters.Where((p, i) => i != index).SelectMany(p => TypeParameters(p.Type)));
				if (invoke.Parameters.SelectMany(p => TypeParameters(p.Type)).Any(tp => tp.OwnerType == SymbolKind.Method && !fromOthers.Contains(tp)))
					return TargetKind.None;
			}
			// overloads that take a lambda with other parameter types at this position
			var kind = TargetKind.Exact;
			foreach (var other in definition.DeclaringTypeDefinition?.GetMethods(m => m.Name == definition.Name && !m.Equals(definition)) ?? Enumerable.Empty<IMethod>())
			{
				if (other.Parameters.Count <= index)
					continue;
				var otherInvoke = other.Parameters[index].Type.GetDelegateInvokeMethod();
				if (otherInvoke == null || otherInvoke.Parameters.Count != invoke.Parameters.Count)
					continue;
				for (int p = 0; p < invoke.Parameters.Count; p++)
				{
					if (invoke.Parameters[p].Type.ReflectionName != otherInvoke.Parameters[p].Type.ReflectionName
						|| invoke.Parameters[p].ReferenceKind != otherInvoke.Parameters[p].ReferenceKind)
						return TargetKind.None;
				}
				kind = TargetKind.ParameterTypes;
			}
			return kind;
		}

		static IEnumerable<ITypeParameter> TypeParameters(IType type)
		{
			switch (type)
			{
				case ITypeParameter tp:
					yield return tp;
					break;
				case ParameterizedType pt:
					foreach (var tp in pt.TypeArguments.SelectMany(TypeParameters))
						yield return tp;
					break;
				case TypeWithElementType withElement:
					foreach (var tp in TypeParameters(withElement.ElementType))
						yield return tp;
					break;
			}
		}
	}
}
