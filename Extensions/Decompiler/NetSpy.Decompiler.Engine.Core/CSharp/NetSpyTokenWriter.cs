/*
    Copyright (C) 2026 NetSpy contributors

    This file is part of NetSpy (a dnSpy fork)

    NetSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    NetSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with NetSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Text;
using NetSpy.Decompiler.CSharp;
using NetSpy.Decompiler.CSharp.OutputVisitor;
using NetSpy.Decompiler.CSharp.Syntax;
using NetSpy.Decompiler.CSharp.Transforms;
using NetSpy.Decompiler.Semantics;
using NetSpy.Decompiler.TypeSystem;
using NetSpy.Decompiler.TypeSystem.Implementation;
using ILVariable = NetSpy.Decompiler.IL.ILVariable;

namespace NetSpy.Decompiler.Engine.Core.CSharp {
	/// <summary>
	/// Writes the modern NetSpy syntax tree to NetSpy's <see cref="IDecompilerOutput"/>: colors,
	/// clickable references (dnlib objects), brace pairs and positions for the debug info.
	/// Based on NetSpy's TextTokenWriter.
	/// </summary>
	sealed class NetSpyTokenWriter : TokenWriter, ILocatable {
		readonly IDecompilerOutput output;
		readonly SymbolConverter converter;
		readonly VariableMapper variables;
		readonly MetadataTextColorProvider colorProvider;
		readonly Stack<AstNode> nodeStack = new Stack<AstNode>();
		readonly Stack<(int start, CodeBracesRangeFlags flags)> curlyBraces = new Stack<(int, CodeBracesRangeFlags)>();
		readonly Stack<int> parentheses = new Stack<int>();
		readonly Stack<int> squareBrackets = new Stack<int>();
		readonly Stack<int> angleBrackets = new Stack<int>();

		public NetSpyTokenWriter(IDecompilerOutput output, SymbolConverter converter, VariableMapper variables, MetadataTextColorProvider colorProvider) {
			this.output = output;
			this.converter = converter;
			this.variables = variables;
			this.colorProvider = colorProvider;
		}

		// ILocatable: the whole text is treated as a single line and the column is the absolute
		// position + 1. DebugInfoCreator converts it back when it builds the debug info.
		public TextLocation Location => new TextLocation(1, output.NextPosition + 1);
		public int Length => output.Length;

		AstNode? CurrentNode => nodeStack.Count == 0 ? null : nodeStack.Peek();

		public override void StartNode(AstNode node) => nodeStack.Push(node);

		public override void EndNode(AstNode node) {
			if (nodeStack.Pop() != node)
				throw new InvalidOperationException();
		}

		#region Identifiers and references
		public override void WriteIdentifier(Identifier identifier) {
			string name = TextWriterTokenWriter.EscapeIdentifier(identifier.Name);
			if (identifier.IsVerbatim || CSharpOutputVisitor.IsKeyword(identifier.Name, identifier))
				name = "@" + name;

			var definition = GetCurrentDefinition();
			if (definition is not null) {
				var reference = converter.GetReference(definition);
				Write(name, reference, DecompilerReferenceFlags.Definition, GetColor(definition, reference));
				return;
			}

			var member = GetCurrentMemberReference();
			if (member is not null) {
				var reference = converter.GetReference(member);
				var color = GetColor(member, reference);
				if (IsDynamicMemberReference(CurrentNode))
					output.Write(name, color);
				else
					Write(name, reference, DecompilerReferenceFlags.None, color);
				return;
			}

			var localDefinition = GetCurrentLocalDefinition(identifier);
			if (localDefinition is not null) {
				var reference = ToLocalReference(localDefinition);
				output.Write(name, reference, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition, GetLocalColor(localDefinition));
				return;
			}

			var localRef = GetCurrentLocalReference();
			if (localRef is not null) {
				var reference = ToLocalReference(localRef);
				output.Write(name, reference, DecompilerReferenceFlags.Local, GetLocalColor(localRef));
				return;
			}

			output.Write(name, GetIdentifierColor(identifier));
		}

		void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) {
			if (reference is null)
				output.Write(text, color);
			else
				output.Write(text, reference, flags, color);
		}

		object ToLocalReference(object obj) => obj is ILVariable v ? variables.Get(v) : obj;

		static object GetLocalColor(object obj) {
			switch (obj) {
			case ILVariable v:
				return v.Kind == NetSpy.Decompiler.IL.VariableKind.Parameter ? BoxedTextColor.Parameter : BoxedTextColor.Local;
			case IMethod m:
				return m.IsStatic ? BoxedTextColor.StaticMethod : BoxedTextColor.InstanceMethod;
			case string:
				return BoxedTextColor.Label;
			}
			return BoxedTextColor.Local;
		}

		/// <summary>
		/// Color for identifiers without a symbol (eg. names in object initializers of anonymous types)
		/// </summary>
		object GetIdentifierColor(Identifier identifier) {
			var node = identifier.Parent;
			switch (node) {
			case LabelStatement:
			case GotoStatement:
				return BoxedTextColor.Label;
			case NamedExpression:
			case NamedArgumentExpression:
				return BoxedTextColor.InstanceProperty;
			case ExternAliasDeclaration:
			case UsingAliasDeclaration:
				return BoxedTextColor.Namespace;
			}
			var symbol = node?.GetSymbol();
			return symbol is null ? BoxedTextColor.Text : GetColor(symbol, null);
		}

		object GetColor(ISymbol symbol, object? reference) {
			if (reference is not null && reference is not NamespaceReference)
				return colorProvider.GetColor(reference);
			switch (symbol) {
			case INamespace:
				return BoxedTextColor.Namespace;
			case ITypeParameter tp:
				return tp.OwnerType == SymbolKind.Method ? BoxedTextColor.MethodGenericParameter : BoxedTextColor.TypeGenericParameter;
			case IType type:
				switch (type.Kind) {
				case TypeKind.Interface: return BoxedTextColor.Interface;
				case TypeKind.Struct: return BoxedTextColor.ValueType;
				case TypeKind.Enum: return BoxedTextColor.Enum;
				case TypeKind.Delegate: return BoxedTextColor.Delegate;
				}
				var td = type.GetDefinition();
				if (td is not null && td.IsStatic)
					return BoxedTextColor.StaticType;
				if (td is not null && td.IsSealed)
					return BoxedTextColor.SealedType;
				return BoxedTextColor.Type;
			case IMethod method:
				if (method.IsExtensionMethod)
					return BoxedTextColor.ExtensionMethod;
				return method.IsStatic ? BoxedTextColor.StaticMethod : BoxedTextColor.InstanceMethod;
			case IField field:
				if (field.DeclaringType.Kind == TypeKind.Enum)
					return BoxedTextColor.EnumField;
				if (field.IsConst)
					return BoxedTextColor.LiteralField;
				return field.IsStatic ? BoxedTextColor.StaticField : BoxedTextColor.InstanceField;
			case IProperty property:
				return property.IsStatic ? BoxedTextColor.StaticProperty : BoxedTextColor.InstanceProperty;
			case IEvent ev:
				return ev.IsStatic ? BoxedTextColor.StaticEvent : BoxedTextColor.InstanceEvent;
			case IVariable:
				return BoxedTextColor.Local;
			}
			return BoxedTextColor.Text;
		}

		ISymbol? GetCurrentMemberReference() {
			var node = CurrentNode;
			if (node is null)
				return null;
			var symbol = node.GetSymbol();
			if (symbol is null && node.Slot?.Kind == Slots.TargetExpression && node.Parent is InvocationExpression)
				symbol = node.Parent.GetSymbol();
			if (symbol is not null && node.Slot?.Kind == Slots.Type && node.Parent is ObjectCreateExpression) {
				var ctorSymbol = node.Parent.GetSymbol();
				if (ctorSymbol is not null)
					symbol = ctorSymbol;
			}
			if (node is IdentifierExpression && node.Slot?.Kind == Slots.TargetExpression && node.Parent is InvocationExpression && symbol is IMember member) {
				if (member.DeclaringType?.Kind == TypeKind.Delegate)
					return null;
			}
			if (symbol is IMethod { IsLocalFunction: true })
				return null;
			// Locals are handled by the local reference code
			if (symbol is IVariable)
				return null;
			return symbol;
		}

		static bool IsDynamicMemberReference(AstNode? node) {
			if (node is null)
				return false;
			var rr = node.Annotation<ResolveResult>();
			if (rr is DynamicMemberResolveResult || rr is DynamicInvocationResolveResult)
				return true;
			if (node.Slot?.Kind == Slots.TargetExpression && node.Parent is InvocationExpression
				&& node.Parent.Annotation<ResolveResult>() is DynamicInvocationResolveResult)
				return true;
			if (node.Annotation<NetSpy.Decompiler.IL.DynamicInvokeConstructorInstruction>() is not null)
				return true;
			if (node.Slot?.Kind == Slots.Type && node.Parent?.Annotation<NetSpy.Decompiler.IL.DynamicInvokeConstructorInstruction>() is not null)
				return true;
			return false;
		}

		object? GetCurrentLocalReference() {
			var node = CurrentNode;
			if (node is null)
				return null;
			var variable = node.Annotation<ILVariableResolveResult>()?.Variable;
			if (variable is not null)
				return variable;

			var letClauseVariable = node.Annotation<LetIdentifierAnnotation>();
			if (letClauseVariable is not null)
				return letClauseVariable;

			if (node is GotoStatement gotoStatement) {
				var method = nodeStack.Select(nd => nd.GetSymbol() as IMethod).FirstOrDefault(mr => mr is not null);
				if (method is not null)
					return method + gotoStatement.Label;
			}

			if (node.Slot?.Kind == Slots.TargetExpression && node.Parent is InvocationExpression) {
				if (node.Parent.GetSymbol() is IMethod { IsLocalFunction: true } localFunction)
					return localFunction.MemberDefinition;
			}

			if (node.GetResolveResult() is MethodGroupResolveResult methodGroup
				&& methodGroup.Methods.FirstOrDefault() is IMethod { IsLocalFunction: true } methodGroupLocalFunction)
				return methodGroupLocalFunction.MemberDefinition;

			return null;
		}

		object? GetCurrentLocalDefinition(Identifier id) {
			var node = CurrentNode;
			if (node is null)
				return null;
			if (node is Identifier && node.Parent is not null)
				node = node.Parent;

			if (node is ParameterDeclaration || node is VariableInitializer || node is CatchClause || node is VariableDesignation) {
				var variable = node.Annotation<ILVariableResolveResult>()?.Variable;
				if (variable is not null)
					return variable;
			}

			if (id.Slot?.Kind == Slots.IntoIdentifier || id.Slot?.Kind == Slots.JoinIdentifier) {
				var variable = id.Annotation<ILVariableResolveResult>()?.Variable;
				if (variable is not null)
					return variable;
			}

			if (node is QueryLetClause) {
				var variable = node.Annotation<LetIdentifierAnnotation>();
				if (variable is not null)
					return variable;
			}

			if (node is LabelStatement label) {
				var method = nodeStack.Select(nd => nd.GetSymbol() as IMethod).FirstOrDefault(mr => mr is not null);
				if (method is not null)
					return method + label.Label;
			}

			if (node is MethodDeclaration && node.Parent is LocalFunctionDeclarationStatement) {
				if (node.Parent.GetResolveResult() is MemberResolveResult localFunction)
					return localFunction.Member.MemberDefinition;
			}

			return null;
		}

		ISymbol? GetCurrentDefinition() {
			var node = CurrentNode;
			if (node is null)
				return null;
			if (node is Identifier)
				node = node.Parent;
			if (node is not null && NetSpy.Decompiler.TextTokenWriter.IsDefinition(ref node))
				return node.GetSymbol();
			return null;
		}
		#endregion

		#region Keywords, tokens, literals
		public override void WriteKeyword(string keyword) {
			var node = CurrentNode;
			if (keyword is "this" or "base") {
				if (node is ConstructorInitializer initializer && initializer.GetSymbol() is IMember member) {
					WriteKeywordReference(keyword, member, DecompilerReferenceFlags.None);
					return;
				}
				if (node is ThisReferenceExpression or BaseReferenceExpression) {
					var type = ((Expression)node).GetResolveResult().Type;
					if (type.Kind != TypeKind.Unknown) {
						WriteKeywordReference(keyword, type, DecompilerReferenceFlags.None);
						return;
					}
				}
			}
			if (keyword == "new" && node is ObjectCreateExpression && node.GetSymbol() is IMember ctor) {
				// Like NetSpy: 'new' is a hidden reference to the constructor
				WriteKeywordReference(keyword, ctor, DecompilerReferenceFlags.Hidden);
				return;
			}
			if (keyword == "override"
				&& node is EntityDeclaration entityDeclaration
				&& entityDeclaration.GetSymbol() is IMember { IsOverride: true } overrideMember
				&& InheritanceHelper.GetBaseMember(overrideMember) is IMember baseMember) {
				WriteKeywordReference(keyword, baseMember, DecompilerReferenceFlags.None);
				return;
			}
			output.Write(keyword, BoxedTextColor.Keyword);
		}

		void WriteKeywordReference(string keyword, object symbol, DecompilerReferenceFlags flags) =>
			Write(keyword, converter.GetReference(symbol), flags, BoxedTextColor.Keyword);

		public override void WriteToken(string token) {
			var node = CurrentNode;
			switch (token) {
			case "{":
				if (node is Interpolation) {
					output.Write(token, BoxedTextColor.Punctuation);
					return;
				}
				curlyBraces.Push((output.NextPosition, GetCurlyBracesFlags(node)));
				output.Write(token, BoxedTextColor.Punctuation);
				return;
			case "}":
				if (node is Interpolation) {
					output.Write(token, BoxedTextColor.Punctuation);
					return;
				}
				int closeStart = output.NextPosition;
				output.Write(token, BoxedTextColor.Punctuation);
				if (curlyBraces.Count != 0) {
					var open = curlyBraces.Pop();
					output.AddBracePair(new TextSpan(open.start, 1), new TextSpan(closeStart, 1), open.flags);
				}
				return;
			case "(":
				parentheses.Push(output.NextPosition);
				WriteTokenCore(token, node);
				return;
			case ")":
				ClosePair(parentheses, token, node, CodeBracesRangeFlags.Parentheses);
				return;
			case "[":
				squareBrackets.Push(output.NextPosition);
				WriteTokenCore(token, node);
				return;
			case "]":
				ClosePair(squareBrackets, token, node, CodeBracesRangeFlags.SquareBrackets);
				return;
			case "<":
				if (IsTypeArgumentListNode(node)) {
					angleBrackets.Push(output.NextPosition);
					output.Write(token, BoxedTextColor.Punctuation);
					return;
				}
				break;
			case ">":
				if (IsTypeArgumentListNode(node) && angleBrackets.Count != 0) {
					ClosePair(angleBrackets, token, node, CodeBracesRangeFlags.AngleBrackets);
					return;
				}
				break;
			}
			if (node is InterpolatedStringExpression && token.IndexOf('"') >= 0) {
				output.Write(token, BoxedTextColor.String);
				return;
			}
			WriteTokenCore(token, node);
		}

		void ClosePair(Stack<int> stack, string token, AstNode? node, CodeBracesRangeFlags flags) {
			int closeStart = output.NextPosition;
			WriteTokenCore(token, node);
			if (stack.Count != 0)
				output.AddBracePair(new TextSpan(stack.Pop(), 1), new TextSpan(closeStart, 1), flags);
		}

		void WriteTokenCore(string token, AstNode? node) {
			var color = GetTokenColor(token);
			// Attach the member reference to the token only if there's no identifier in the
			// current node, eg. operators (user defined operators), indexers, delegate invocations
			var member = GetCurrentMemberReference();
			if (member is not null && node is not null && node.GetChild(Slots.Identifier) is null && !IsDynamicMemberReference(node)) {
				var reference = converter.GetReference(member);
				Write(token, reference, DecompilerReferenceFlags.None, color);
				return;
			}
			output.Write(token, color);
		}

		static object GetTokenColor(string token) {
			switch (token) {
			case "(": case ")": case "[": case "]": case "{": case "}":
			case ",": case ";": case ".": case ":": case "::":
				return BoxedTextColor.Punctuation;
			}
			return BoxedTextColor.Operator;
		}

		static bool IsTypeArgumentListNode(AstNode? node) =>
			node is AstType || node is EntityDeclaration || node is DelegateDeclaration ||
			node is IdentifierExpression || node is MemberReferenceExpression || node is TypeReferenceExpression ||
			node is PointerReferenceExpression || node is InvocationExpression;

		static CodeBracesRangeFlags GetCurlyBracesFlags(AstNode? node) {
			switch (node) {
			case NamespaceDeclaration:
				return CodeBracesRangeFlags.NamespaceBraces;
			case TypeDeclaration td:
				switch (td.ClassType) {
				case ClassType.Interface: return CodeBracesRangeFlags.InterfaceBraces;
				case ClassType.Struct:
				case ClassType.RecordStruct:
				case ClassType.Enum:
					return CodeBracesRangeFlags.ValueTypeBraces;
				}
				return CodeBracesRangeFlags.TypeBraces;
			case PropertyDeclaration:
			case IndexerDeclaration:
				return CodeBracesRangeFlags.PropertyBraces;
			case CustomEventDeclaration:
				return CodeBracesRangeFlags.EventBraces;
			case SwitchStatement:
			case SwitchExpression:
				return CodeBracesRangeFlags.SwitchBraces;
			case BlockStatement block:
				return GetBlockFlags(block);
			}
			return CodeBracesRangeFlags.CurlyBraces;
		}

		static CodeBracesRangeFlags GetBlockFlags(BlockStatement block) {
			var parent = block.Parent;
			switch (parent) {
			case MethodDeclaration md:
				return md.Parent is LocalFunctionDeclarationStatement ? CodeBracesRangeFlags.LocalFunctionBraces : CodeBracesRangeFlags.MethodBraces;
			case Accessor:
				return CodeBracesRangeFlags.AccessorBraces;
			case ConstructorDeclaration:
				return CodeBracesRangeFlags.ConstructorBraces;
			case DestructorDeclaration:
				return CodeBracesRangeFlags.DestructorBraces;
			case OperatorDeclaration:
				return CodeBracesRangeFlags.OperatorBraces;
			case LambdaExpression:
			case AnonymousMethodExpression:
				return CodeBracesRangeFlags.AnonymousMethodBraces;
			case IfElseStatement:
				return CodeBracesRangeFlags.ConditionalBraces;
			case ForStatement:
			case ForeachStatement:
			case WhileStatement:
			case DoWhileStatement:
				return CodeBracesRangeFlags.LoopBraces;
			case TryCatchStatement tcs:
				return block == tcs.FinallyBlock ? CodeBracesRangeFlags.FinallyBraces : CodeBracesRangeFlags.TryBraces;
			case CatchClause:
				return CodeBracesRangeFlags.CatchBraces;
			case LockStatement:
				return CodeBracesRangeFlags.LockBraces;
			case UsingStatement:
				return CodeBracesRangeFlags.UsingBraces;
			case FixedStatement:
				return CodeBracesRangeFlags.FixedBraces;
			case SwitchSection:
				return CodeBracesRangeFlags.CaseBraces;
			}
			return CodeBracesRangeFlags.OtherBlockBraces;
		}

		public override void WritePrimitiveValue(object? value, LiteralFormat format = LiteralFormat.None) {
			var sw = new StringWriter();
			new TextWriterTokenWriter(sw).WritePrimitiveValue(value, format);
			var text = sw.ToString();

			int start = output.NextPosition;
			switch (value) {
			case string:
				output.Write(text, format == LiteralFormat.VerbatimStringLiteral ? BoxedTextColor.VerbatimString : BoxedTextColor.String);
				if (text.Length >= 2 && text[text.Length - 1] == '"')
					output.AddBracePair(new TextSpan(start + text.IndexOf('"'), 1), new TextSpan(start + text.Length - 1, 1), CodeBracesRangeFlags.DoubleQuotes);
				return;
			case char:
				output.Write(text, BoxedTextColor.Char);
				if (text.Length >= 2)
					output.AddBracePair(new TextSpan(start, 1), new TextSpan(start + text.Length - 1, 1), CodeBracesRangeFlags.SingleQuotes);
				return;
			case null:
			case bool:
				output.Write(text, BoxedTextColor.Keyword);
				return;
			}
			output.Write(text, BoxedTextColor.Number);
		}

		public override void WriteInterpolatedText(string text) =>
			output.Write(TextWriterTokenWriter.ConvertString(text), BoxedTextColor.String);

		public override void WritePrimitiveType(string type) {
			switch (type) {
			case "new":
				output.Write(type, BoxedTextColor.Keyword);
				int start = output.NextPosition;
				output.Write("(", BoxedTextColor.Punctuation);
				int end = output.NextPosition;
				output.Write(")", BoxedTextColor.Punctuation);
				output.AddBracePair(new TextSpan(start, 1), new TextSpan(end, 1), CodeBracesRangeFlags.Parentheses);
				return;
			case "bool":
			case "byte":
			case "sbyte":
			case "short":
			case "ushort":
			case "int":
			case "uint":
			case "long":
			case "ulong":
			case "float":
			case "double":
			case "decimal":
			case "char":
			case "string":
			case "object":
			case "nint":
			case "nuint":
				var node = CurrentNode;
				ISymbol? symbol;
				if (node?.Slot?.Kind == Slots.Type && node.Parent is ObjectCreateExpression)
					symbol = node.Parent.GetSymbol();
				else
					symbol = node?.GetSymbol();
				if (symbol is not null) {
					WriteKeywordReference(type, symbol, DecompilerReferenceFlags.None);
					return;
				}
				break;
			}
			output.Write(type, BoxedTextColor.Keyword);
		}
		#endregion

		#region Whitespace, comments
		public override void Space() => output.Write(" ", BoxedTextColor.Text);
		public override void Indent() => output.IncreaseIndent();
		public override void Unindent() => output.DecreaseIndent();
		public override void NewLine() => output.WriteLine();

		public override void WriteComment(CommentType commentType, string content) {
			switch (commentType) {
			case CommentType.SingleLine:
				output.Write("//" + content, BoxedTextColor.Comment);
				output.WriteLine();
				break;
			case CommentType.MultiLine:
				output.Write("/*" + content + "*/", BoxedTextColor.Comment);
				break;
			case CommentType.Documentation:
				output.Write("///", BoxedTextColor.XmlDocCommentDelimiter);
				output.WriteXmlDoc(content);
				output.WriteLine();
				break;
			default:
				output.Write(content, BoxedTextColor.Comment);
				break;
			}
		}

		public override void WritePreProcessorDirective(PreProcessorDirectiveType type, string? argument) {
			output.Write("#", BoxedTextColor.PreprocessorKeyword);
			output.Write(type.ToString().ToLowerInvariant(), BoxedTextColor.PreprocessorKeyword);
			if (!string.IsNullOrEmpty(argument)) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write(argument!, BoxedTextColor.PreprocessorText);
			}
			output.WriteLine();
		}
		#endregion
	}
}
