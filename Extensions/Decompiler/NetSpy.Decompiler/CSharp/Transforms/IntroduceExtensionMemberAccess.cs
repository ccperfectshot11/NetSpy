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

using System.Linq;
using System.Reflection;

using NetSpy.Decompiler.CSharp.Resolver;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.Semantics;
using NetSpy.Decompiler.TypeSystem;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: C# 14 extension members at their call sites. The compiler calls the implementation methods
	/// of the static class, so the decompiled code shows "Extensions.get_Count(list)" and "Extensions.Create(1)";
	/// the source wrote them as members of the receiver:
	/// <code>
	/// Extensions.get_Count(list)       → list.Count
	/// Extensions.set_Count(list, 5);   → list.Count = 5;
	/// Extensions.Create(1)             → ReceiverType.Create(1)   (static extension member)
	/// Extensions.get_Empty()           → ReceiverType.Empty      (static extension property)
	/// </code>
	/// (Instance extension methods are already turned into "receiver.M()" by <see cref="IntroduceExtensionMethods"/>.)
	/// </summary>
	public sealed class IntroduceExtensionMemberAccess : DepthFirstAstVisitor, IAstTransform
	{
		TransformContext context = null!;
		CSharpConversions conversions = null!;

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.ExtensionMembers)
				return;
			this.context = context;
			this.conversions = CSharpConversions.Get(context.TypeSystem);
			rootNode.AcceptVisitor(this);
		}

		public override void VisitInvocationExpression(InvocationExpression invocation)
		{
			base.VisitInvocationExpression(invocation);
			if (invocation.GetSymbol() is not IMethod method || method.MemberDefinition is not IMethod definition)
				return;
			if (definition.DeclaringTypeDefinition?.ExtensionInfo?.InfoOfImplementationMember(definition) is not { } info)
				return;
			var member = info.ExtensionMember;
			var arguments = invocation.Arguments.ToList();
			if (arguments.Any(a => a is NamedArgumentExpression or DirectionExpression))
				return;

			if (member.IsAccessor)
			{
				if (member.AccessorOwner is not IProperty property || property.IsIndexer)
					return;
				int receiverCount = member.IsStatic ? 0 : 1;
				Expression? target = member.IsStatic ? StaticReceiver(info, property.Name) : InstanceReceiver(method, arguments, property.Name);
				if (target is null)
					return;
				if (member.AccessorKind == MethodSemanticsAttributes.Getter && arguments.Count == receiverCount)
				{
					context.Step("Introduce extension property access", invocation);
					if (!member.IsStatic)
						target = target.Detach();
					invocation.ReplaceWith(new MemberReferenceExpression(target, property.Name));
				}
				else if (member.AccessorKind == MethodSemanticsAttributes.Setter && arguments.Count == receiverCount + 1
					&& invocation.Parent is ExpressionStatement)
				{
					context.Step("Introduce extension property assignment", invocation);
					if (!member.IsStatic)
						target = target.Detach();
					var value = arguments[receiverCount].Detach();
					invocation.ReplaceWith(new AssignmentExpression(new MemberReferenceExpression(target, property.Name), value));
				}
			}
			else if (member.IsStatic)
			{
				// "Extensions.Create(args)" / "Create(args)" → "ReceiverType.Create(args)"
				if (StaticReceiver(info, member.Name) is not { } typeTarget)
					return;
				switch (invocation.Target)
				{
					case MemberReferenceExpression { Target: TypeReferenceExpression } mre:
						context.Step("Introduce static extension member call", invocation);
						mre.Target = typeTarget;
						break;
					case IdentifierExpression id:
						context.Step("Introduce static extension member call", invocation);
						invocation.Target = new MemberReferenceExpression(typeTarget, id.Identifier, id.TypeArguments.Detach());
						break;
				}
			}
		}

		/// <summary>The receiver argument of an instance extension member, if "receiver.Name" means the same call.</summary>
		Expression? InstanceReceiver(IMethod method, System.Collections.Generic.List<Expression> arguments, string name)
		{
			if (arguments.Count == 0 || method.Parameters.Count == 0)
				return null;
			var receiver = arguments[0];
			// no natural type: "null.Count" or "(x => x).Count" don't compile
			if (receiver is NullReferenceExpression or LambdaExpression or AnonymousMethodExpression or CollectionExpression
				|| receiver is PrimitiveExpression { Value: null })
				return null;
			var receiverType = receiver.GetResolveResult().Type;
			if (receiverType.Kind == TypeKind.Unknown)
				return null;
			// extension receivers only allow identity, reference and boxing conversions
			// (the called method is specialized: "List<int>", not the definition's "List<T>")
			var conversion = conversions.ImplicitConversion(receiverType, method.Parameters[0].Type);
			if (!(conversion.IsIdentityConversion || conversion.IsReferenceConversion || conversion.IsBoxingConversion))
				return null;
			// a real member with the same name would win over the extension member
			if (HasMember(receiverType, name))
				return null;
			return receiver;
		}

		/// <summary>"ReceiverType" of a static extension member (non-generic extension groups only).</summary>
		Expression? StaticReceiver(ExtensionMemberInfo info, string name)
		{
			if (info.ExtensionGroupingTypeParameters.Count != 0 || info.ExtensionMarkerMethod.Parameters.Count != 1)
				return null;
			var receiverType = info.ExtensionMarkerMethod.Parameters[0].Type;
			if (receiverType.Kind == TypeKind.Unknown || HasMember(receiverType, name))
				return null;
			return new TypeReferenceExpression(context.TypeSystemAstBuilder.ConvertType(receiverType));
		}

		static bool HasMember(IType type, string name) =>
			type.GetMembers(m => m.Name == name).Any() || type.GetNestedTypes(t => t.Name == name).Any();
	}
}
