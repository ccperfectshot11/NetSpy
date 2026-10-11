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

using NetSpy.Decompiler.CSharp.Syntax;

namespace NetSpy.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: marks single-statement bodies that are written as "=> expr;" (modern C# style).
	/// The AST keeps the block; <see cref="OutputVisitor.CSharpOutputVisitor"/> writes a marked block
	/// as an expression body, so every other transform and the debugger still see a normal statement.
	/// </summary>
	public sealed class ExpressionBodiedMembers : DepthFirstAstVisitor, IAstTransform
	{
		/// <summary>Longest expression (in characters, written on one line) that becomes an expression body.</summary>
		const int MaxExpressionLength = 100;

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.ExpressionBodiedMembers)
				return;
			rootNode.AcceptVisitor(this);
		}

		public override void VisitMethodDeclaration(MethodDeclaration methodDeclaration)
		{
			Mark(methodDeclaration.Body);
			base.VisitMethodDeclaration(methodDeclaration);
		}

		public override void VisitOperatorDeclaration(OperatorDeclaration operatorDeclaration)
		{
			Mark(operatorDeclaration.Body);
			base.VisitOperatorDeclaration(operatorDeclaration);
		}

		public override void VisitConstructorDeclaration(ConstructorDeclaration constructorDeclaration)
		{
			// "C() : base(x) => ..." is valid but unusual
			if (constructorDeclaration.Initializer is null)
				Mark(constructorDeclaration.Body);
			base.VisitConstructorDeclaration(constructorDeclaration);
		}

		public override void VisitDestructorDeclaration(DestructorDeclaration destructorDeclaration)
		{
			Mark(destructorDeclaration.Body);
			base.VisitDestructorDeclaration(destructorDeclaration);
		}

		public override void VisitAccessor(Accessor accessor)
		{
			Mark(accessor.Body);
			base.VisitAccessor(accessor);
		}

		static void Mark(BlockStatement? body)
		{
			if (body is null || body.Statements.Count != 1 || GetExpression(body) is not { } expression)
				return;
			// NetSpy: accessors: the PDB columns said which of "=> x", "{ return x; }" or a block it was
			switch (AnnotateAccessorStyles.StyleOf(body))
			{
				case AccessorStyle.ExpressionBody:
					body.AddAnnotation(ExpressionBodyAnnotation.Instance);
					return;
				case AccessorStyle.SingleLineBlock:
					if (!body.Statements.Single().ToString().Trim().Contains("\n"))
						body.AddAnnotation(SingleLineBlockAnnotation.Instance);
					return;
				case AccessorStyle.Block:
					return;
			}
			// the PDB says how the original was written
			if (body.Annotation<BodyStyleAnnotation>() is { } style)
			{
				if (style.IsExpressionBody)
					body.AddAnnotation(ExpressionBodyAnnotation.Instance);
				return;
			}
			// "=> switch" bodies are idiomatic even when they span several lines
			if (expression is not SwitchExpression)
			{
				var text = expression.ToString();
				if (text.Length > MaxExpressionLength || text.Contains("\n"))
					return;
			}
			body.AddAnnotation(ExpressionBodyAnnotation.Instance);
		}

		/// <summary>
		/// The expression of the single statement of <paramref name="body"/>, if it can be an expression body:
		/// "return x;" → x, "x = y;" → x = y, "throw e;" → e (written as the throw expression "throw e").
		/// </summary>
		public static Expression? GetExpression(BlockStatement body)
		{
			if (body.Statements.Count != 1)
				return null;
			foreach (var statement in body.Statements)
			{
				return statement switch {
					ReturnStatement r => r.Expression,
					ExpressionStatement e => e.Expression,
					ThrowStatement t => t.Expression,
					_ => null
				};
			}
			return null;
		}
	}

	/// <summary>
	/// NetSpy: the annotated method body is written as "=> expr;".
	/// </summary>
	/// <summary>
	/// NetSpy: the annotated body is written on one line: "{ return x; }".
	/// </summary>
	public sealed class SingleLineBlockAnnotation
	{
		public static readonly SingleLineBlockAnnotation Instance = new SingleLineBlockAnnotation();

		SingleLineBlockAnnotation()
		{
		}
	}

	/// <summary>
	/// NetSpy: the annotated field had an empty line before it in the source (fields are otherwise grouped).
	/// </summary>
	public sealed class BlankLineBeforeFieldAnnotation
	{
		public static readonly BlankLineBeforeFieldAnnotation Instance = new BlankLineBeforeFieldAnnotation();

		BlankLineBeforeFieldAnnotation()
		{
		}
	}

	/// <summary>
	/// NetSpy: the annotated member access of a call chain starts a new line, one level deeper (".Where(...)").
	/// </summary>
	public sealed class ChainWrapAnnotation
	{
		public static readonly ChainWrapAnnotation Instance = new ChainWrapAnnotation();

		ChainWrapAnnotation()
		{
		}
	}

	public sealed class ExpressionBodyAnnotation
	{
		public static readonly ExpressionBodyAnnotation Instance = new ExpressionBodyAnnotation();

		ExpressionBodyAnnotation()
		{
		}
	}
}
