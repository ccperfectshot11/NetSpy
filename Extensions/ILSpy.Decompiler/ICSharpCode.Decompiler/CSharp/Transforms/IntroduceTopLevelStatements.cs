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

using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// NetSpy: C# 9 top-level statements. The compiler turns them into
	/// "[CompilerGenerated] class Program { static ... &lt;Main&gt;$(string[] args) { statements } }";
	/// this writes the statements at the top of the file again.
	/// Runs twice: <see cref="Phase.Prepare"/> early (drops the attribute and return type, so no
	/// usings are added for them), <see cref="Phase.Move"/> after the layout transforms.
	/// </summary>
	public sealed class IntroduceTopLevelStatements : IAstTransform
	{
		public enum Phase { Prepare, Move }

		readonly Phase phase;

		public IntroduceTopLevelStatements(Phase phase) => this.phase = phase;

		public void Run(AstNode rootNode, TransformContext context)
		{
			if (!context.Settings.TopLevelStatements || rootNode is not SyntaxTree syntaxTree)
				return;
			foreach (var type in syntaxTree.Children.OfType<TypeDeclaration>().ToList())
			{
				if (phase == Phase.Prepare)
				{
					if (FindMain(type) is { } main)
					{
						type.AddAnnotation(TopLevelProgramAnnotation.Instance);
						foreach (var section in type.Attributes.ToList())
						{
							if (section.Attributes.All(a => a.Type.ToString().Split('.').Last() is "CompilerGenerated" or "CompilerGeneratedAttribute"))
								section.Remove();
						}
						main.ReturnType = new PrimitiveType("void");
					}
				}
				else if (type.Annotation<TopLevelProgramAnnotation>() is not null && type.Members.SingleOrDefault() is MethodDeclaration { Body: { } body })
				{
					foreach (var statement in body.Statements.ToList())
					{
						statement.Remove();
						syntaxTree.InsertChildBefore(type, statement, Slots.Member);
					}
					type.Remove();
				}
			}
		}

		/// <summary>The '&lt;Main&gt;$' method of a compiler-generated global Program class with nothing else in it.</summary>
		static MethodDeclaration? FindMain(TypeDeclaration type)
		{
			if (type.GetSymbol() is not ITypeDefinition { Name: "Program", Namespace: "", DeclaringTypeDefinition: null } definition
				|| !definition.HasAttribute(KnownAttribute.CompilerGenerated))
				return null;
			var main = type.Members.OfType<MethodDeclaration>().FirstOrDefault(m => m.GetSymbol() is IMethod { Name: "<Main>$", IsStatic: true });
			if (main?.GetSymbol() is not IMethod method || method.Parameters.Count != 1 || method.Parameters[0].Name != "args")
				return null;
			// everything else must be compiler-generated too (the '<Main>' entry point of an async program, ...)
			// (and the implicit default constructor)
			if (type.Members.Any(m => m != main && !(m.GetSymbol() is IEntity { Name: var name } && name.StartsWith("<", System.StringComparison.Ordinal))
				&& !(m is ConstructorDeclaration { Parameters.Count: 0 })))
				return null;
			return main;
		}
	}

	sealed class TopLevelProgramAnnotation
	{
		public static readonly TopLevelProgramAnnotation Instance = new TopLevelProgramAnnotation();
	}
}
