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

using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.Semantics;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: nameof(x) compiles to the string "x". Strings that name something in scope where a name is
	/// expected are written as nameof again:
	/// <list type="bullet">
	/// <item>throw new ArgumentNullException("value") → nameof(value) (arguments of 'paramName' / 'propertyName' parameters)</item>
	/// <item>[CallerArgumentExpression("condition")] → nameof(condition) (attribute strings naming a parameter)</item>
	/// <item>[CollectionBuilder(typeof(Bag), "Create")] → nameof(Bag.Create)</item>
	/// </list>
	/// </summary>
	public sealed class IntroduceNameof : DepthFirstAstVisitor, IAstTransform
	{
		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.NameofExpressions)
				return;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitInvocationExpression(InvocationExpression invocation)
		{
			base.VisitInvocationExpression(invocation);
			if (invocation.GetSymbol() is IMethod method)
				ReplaceArguments(invocation, invocation.Arguments, method);
		}

		public override void VisitObjectCreateExpression(ObjectCreateExpression creation)
		{
			base.VisitObjectCreateExpression(creation);
			if (creation.GetSymbol() is IMethod constructor)
				ReplaceArguments(creation, creation.Arguments, constructor);
		}

		void ReplaceArguments(AstNode call, AstNodeCollection<Expression> arguments, IMethod method)
		{
			var args = arguments.ToList();
			for (int i = 0; i < args.Count && i < method.Parameters.Count; i++)
			{
				if (args[i] is not PrimitiveExpression { Value: string text } literal)
					continue;
				switch (method.Parameters[i].Name)
				{
					case "paramName" or "parameterName" or "argumentName":
						if (ParametersInScope(call).Contains(text))
							literal.ReplaceWith(Nameof(new IdentifierExpression(text)));
						break;
					case "propertyName":
						if (call.GetParent<TypeDeclaration>()?.GetSymbol() is ITypeDefinition type && type.GetProperties(p => p.Name == text).Any())
							literal.ReplaceWith(Nameof(new IdentifierExpression(text)));
						break;
				}
			}
		}

		public override void VisitAttribute(Syntax.Attribute attribute)
		{
			base.VisitAttribute(attribute);
			var args = attribute.Arguments.ToList();
			// [A(typeof(T), "Member")]
			var typeOfType = args.OfType<TypeOfExpression>().Select(t => t.Type.GetResolveResult().Type.GetDefinition()).FirstOrDefault(t => t != null);
			var parameters = ParametersOfAttributeTarget(attribute);
			foreach (var arg in args)
			{
				var literal = arg is NamedExpression named ? named.Expression as PrimitiveExpression : arg as PrimitiveExpression;
				if (literal?.Value is not string text || text.Length == 0)
					continue;
				if (parameters.Contains(text))
				{
					literal.ReplaceWith(Nameof(new IdentifierExpression(text)));
				}
				else if (typeOfType != null && typeOfType.GetMembers(m => m.Name == text).Any())
				{
					var typeSyntax = args.OfType<TypeOfExpression>().First().Type.Clone();
					literal.ReplaceWith(Nameof(new MemberReferenceExpression(new TypeReferenceExpression(typeSyntax), text)));
				}
			}
		}

		static Expression Nameof(Expression argument)
		{
			return new InvocationExpression(new IdentifierExpression("nameof"), argument);
		}

		/// <summary>The parameter names of the method/lambda/accessor the node is in ("value" in a setter).</summary>
		static HashSet<string> ParametersInScope(AstNode node)
		{
			var names = new HashSet<string>();
			for (var n = node.Parent; n != null; n = n.Parent)
			{
				switch (n)
				{
					case LambdaExpression lambda:
						names.UnionWith(lambda.Parameters.Select(p => p.Name).OfType<string>());
						break;
					case AnonymousMethodExpression anonymous:
						names.UnionWith(anonymous.Parameters.Select(p => p.Name).OfType<string>());
						break;
					case Accessor accessor:
						// (a local function is a MethodDeclaration: the EntityDeclaration case)
						var kind = accessor.Slot?.Kind;
						if (kind == Slots.Setter || kind == Slots.AddAccessor || kind == Slots.RemoveAccessor)
							names.Add("value");
						break;
					case IndexerDeclaration indexer:
						names.UnionWith(indexer.Parameters.Select(p => p.Name).OfType<string>());
						return names;
					case EntityDeclaration entity:
						names.UnionWith(entity.GetChildren(Slots.Parameter).OfType<ParameterDeclaration>().Select(p => p.Name).OfType<string>());
						return names;
				}
			}
			return names;
		}

		/// <summary>The parameters an attribute of a method, parameter or return value can name.</summary>
		static HashSet<string> ParametersOfAttributeTarget(Syntax.Attribute attribute)
		{
			var section = attribute.Parent as AttributeSection;
			var target = section?.Parent;
			if (target is ParameterDeclaration)
				target = target.Parent;
			if (target is EntityDeclaration entity)
				return new HashSet<string>(entity.GetChildren(Slots.Parameter).OfType<ParameterDeclaration>().Select(p => p.Name).OfType<string>());
			return new HashSet<string>();
		}
	}
}
