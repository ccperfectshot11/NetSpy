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
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using XmlDocLoader = dnSpy.Contracts.Decompiler.XmlDoc.XmlDocLoader;
using dnSpy.Contracts.Text;
using System.Diagnostics;
using dnSpy.Decompiler.CSharp;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using dnSpy.Decompiler.ILSpy.Core.Text;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;
using IMethod = ICSharpCode.Decompiler.TypeSystem.IMethod;
using IType = ICSharpCode.Decompiler.TypeSystem.IType;
using IField = ICSharpCode.Decompiler.TypeSystem.IField;
using SRM = System.Reflection.Metadata;
using ILSpyDecompiler = ICSharpCode.Decompiler.CSharp.CSharpDecompiler;
using ILSpySettings = ICSharpCode.Decompiler.DecompilerSettings;

namespace dnSpy.Decompiler.ILSpy.Core.CSharp {
	sealed class DecompilerProvider : IDecompilerProvider {
		readonly DecompilerSettingsService decompilerSettingsService;

		// Keep the default ctor. It's used by NetSpy.Console.exe
		public DecompilerProvider()
			: this(DecompilerSettingsService.__Instance_DONT_USE) {
		}

		public DecompilerProvider(DecompilerSettingsService decompilerSettingsService) {
			Debug2.Assert(decompilerSettingsService is not null);
			this.decompilerSettingsService = decompilerSettingsService ?? throw new ArgumentNullException(nameof(decompilerSettingsService));
		}

		public IEnumerable<IDecompiler> Create() {
			yield return new CSharpDecompiler(decompilerSettingsService.CSharpDecompilerSettings, DecompilerConstants.CSHARP_ILSPY_ORDERUI);
		}
	}

	/// <summary>
	/// C# decompiler using the modern ILSpy engine (vendored in Extensions/ILSpy.Decompiler/ICSharpCode.Decompiler)
	/// </summary>
	sealed class CSharpDecompiler : DecompilerBase {
		readonly CSharpDecompilerSettings langSettings;

		public CSharpDecompiler(CSharpDecompilerSettings langSettings, double orderUI) {
			this.langSettings = langSettings;
			OrderUI = orderUI;
		}

		public override DecompilerSettingsBase Settings => langSettings;
		public override string ContentTypeString => ContentTypesInternal.CSharpILSpy;
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_CSHARP;
		public override string UniqueNameUI => "C#";
		public override double OrderUI { get; }
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_CSHARP;
		public override Guid UniqueGuid => DecompilerConstants.LANGUAGE_CSHARP_ILSPY;
		public override string FileExtension => ".cs";
		public override string? ProjectFileExtension => ".csproj";

		#region Members
		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, method);
			if (method.IsConstructor && !method.DeclaringType.IsValueType) {
				// Also show the field initializers that are part of the constructor
				Run(method.Module, output, ctx, CreateSettings(isSingleMember: true),
					d => d.Decompile(CollectFieldsAndCtors(method.DeclaringType, method.IsStatic)),
					new SelectCtorTransform(method));
			}
			else
				Run(method.Module, output, ctx, CreateSettings(isSingleMember: true), d => d.Decompile(ToHandle(method)));
		}

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, property);
			Run(property.Module, output, ctx, CreateSettings(isSingleMember: true), d => d.Decompile(ToHandle(property)));
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, field);
			if (field.IsLiteral)
				Run(field.Module, output, ctx, CreateSettings(isSingleMember: true), d => d.Decompile(ToHandle(field)));
			else {
				// Also decompile the constructors so the field initializer can be shown
				Run(field.Module, output, ctx, CreateSettings(isSingleMember: true),
					d => d.Decompile(CollectFieldsAndCtors(field.DeclaringType, field.IsStatic)),
					new SelectFieldTransform(field));
			}
		}

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, ev);
			Run(ev.Module, output, ctx, CreateSettings(isSingleMember: true), d => d.Decompile(ToHandle(ev)));
		}

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) =>
			Run(type.Module, output, ctx, CreateSettings(), d => d.Decompile(ToHandle(type)));

		public override void Decompile(AssemblyDef asm, IDecompilerOutput output, DecompilationContext ctx) {
			WriteAssembly(asm, output, ctx);
			using (ctx.DisableAssemblyLoad())
				Run(asm.ManifestModule, output, ctx, CreateSettings(), d => d.DecompileModuleAndAssemblyAttributes());
		}

		public override void Decompile(ModuleDef mod, IDecompilerOutput output, DecompilationContext ctx) {
			WriteModule(mod, output, ctx);
			using (ctx.DisableAssemblyLoad())
				Run(mod, output, ctx, CreateSettings(), d => d.DecompileModuleAndAssemblyAttributes());
		}

		static SRM.EntityHandle ToHandle(IMDTokenProvider def) => MetadataTokens.EntityHandle((int)def.MDToken.Raw);

		static List<SRM.EntityHandle> CollectFieldsAndCtors(TypeDef type, bool isStatic) {
			var members = new List<SRM.EntityHandle>();
			foreach (var field in type.Fields) {
				if (field.IsStatic == isStatic)
					members.Add(ToHandle(field));
			}
			foreach (var ev in type.Events) {
				if ((ev.AddMethod ?? ev.RemoveMethod)?.IsStatic == isStatic)
					members.Add(ToHandle(ev));
			}
			foreach (var prop in type.Properties) {
				var accessor = prop.GetMethod ?? prop.SetMethod;
				if (accessor is not null && accessor.IsStatic == isStatic && accessor.Parameters.Count == (accessor.IsStatic ? 0 : 1) + (accessor == prop.SetMethod ? 1 : 0))
					members.Add(ToHandle(prop));
			}
			foreach (var ctor in type.Methods) {
				if (ctor.IsConstructor && ctor.IsStatic == isStatic)
					members.Add(ToHandle(ctor));
			}
			return members;
		}
		#endregion

		#region Engine
		ILSpySettings CreateSettings(bool isSingleMember = false) {
			var settings = langSettings.Settings.Clone();
			if (isSingleMember) {
				// A single member doesn't need using directives (they'd be shown above the member)
				settings.UsingDeclarations = false;
			}
			return settings;
		}

		/// <summary>
		/// Creates the ILSpy decompiler for a module, runs <paramref name="decompile"/> and writes the syntax tree
		/// </summary>
		void Run(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx, ILSpySettings settings, Func<ILSpyDecompiler, SyntaxTree> decompile, params IAstTransform[] extraTransforms) {
			var decompiler = CreateILSpyDecompiler(module, ctx, settings);
			foreach (var transform in extraTransforms)
				decompiler.AstTransforms.Add(transform);
			var syntaxTree = decompile(decompiler);
			WriteSyntaxTree(module, output, ctx, settings, decompiler, syntaxTree);
		}

		internal static ILSpyDecompiler CreateILSpyDecompiler(ModuleDef module, DecompilationContext ctx, ILSpySettings settings) {
			var typeSystem = TypeSystemCache.Get(module, settings);
			return new ILSpyDecompiler(typeSystem, settings) {
				CancellationToken = ctx.CancellationToken,
				DebugInfoProvider = new DnlibDebugInfoProvider(module),
				DocumentationProvider = settings.ShowXmlDocumentation ? new DnSpyDocumentationProvider() : null,
			};
		}

		void WriteSyntaxTree(ModuleDef module, IDecompilerOutput output, DecompilationContext ctx, ILSpySettings settings, ILSpyDecompiler decompiler, SyntaxTree syntaxTree) {
			syntaxTree.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });

			var converter = new SymbolConverter(module);
			var variables = new VariableMapper(converter);
			var writer = new DnSpyTokenWriter(output, converter, variables, MetadataTextColorProvider);
			// Same decorators as TokenWriter.CreateWriterThatSetsLocationsInAST(): the node locations
			// are needed to compute the debug info (IL offset <-> text span)
			var tokenWriter = TokenWriter.InsertRequiredSpaces(TokenWriter.WrapInWriterThatSetsLocationsInAST(writer));
			syntaxTree.AcceptVisitor(new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions));

			if (ctx.CalculateILSpans) {
				var usings = syntaxTree.Descendants.OfType<UsingDeclaration>().Select(a => a.Namespace).ToArray();
				DebugInfoCreator.AddDebugInfo(output, decompiler, syntaxTree, converter, variables, langSettings.Settings.GetHashCode() ^ langSettings.Version, usings);
			}
		}
		#endregion

		#region Names, tooltips
		sealed class OutputTextColorWriter : ITextColorWriter {
			readonly IDecompilerOutput output;
			public OutputTextColorWriter(IDecompilerOutput output) => this.output = output;
			public void Write(object color, string? text) {
				if (!string.IsNullOrEmpty(text))
					output.Write(text!, color);
			}
			public void Write(TextColor color, string? text) => Write(color.Box(), text);
		}

		protected override void TypeToString(IDecompilerOutput output, ITypeDefOrRef? type, bool includeNamespace, IHasCustomAttribute? typeAttributes = null) {
			if (type is null)
				return;
			var options = FormatterOptions.ShowIntrinsicTypeKeywords;
			if (includeNamespace)
				options |= FormatterOptions.ShowNamespaces;
			new CSharpFormatter(new OutputTextColorWriter(output), options, null).Write(type);
		}

		protected override void FormatTypeName(IDecompilerOutput output, TypeDef type) {
			if (type is null)
				throw new ArgumentNullException(nameof(type));
			new CSharpFormatter(new OutputTextColorWriter(output), FormatterOptions.Default & ~(FormatterOptions.ShowNamespaces | FormatterOptions.ShowDeclaringTypes), null).Write(type);
		}

		public override bool ShowMember(IMemberRef member) {
			if (member is MethodDef md && (md.IsGetter || md.IsSetter || md.IsAddOn || md.IsRemoveOn))
				return true;
			if (langSettings.Settings.ForceShowAllMembers())
				return true;
			if (member is not IMemberDef def || def.Module is null)
				return true;
			try {
				var file = DnlibMetadata.GetMetadataFile(def.Module);
				return !ILSpyDecompiler.MemberIsHidden(file, ToHandle(def), langSettings.Settings);
			}
			catch (Exception) {
				return true;
			}
		}
		#endregion

		#region Assembly editor support (Edit Method / Edit Class / Edit Assembly Attributes)
		public override bool CanDecompile(DecompilationType decompilationType) {
			switch (decompilationType) {
			case DecompilationType.PartialType:
			case DecompilationType.AssemblyInfo:
			case DecompilationType.TypeMethods:
			case DecompilationType.WholeProject:
				return true;
			}
			return base.CanDecompile(decompilationType);
		}

		public override void Decompile(DecompilationType decompilationType, object data) {
			switch (decompilationType) {
			case DecompilationType.PartialType:
				DecompilePartial((DecompilePartialType)data);
				return;
			case DecompilationType.AssemblyInfo:
				DecompileAssemblyInfo((DecompileAssemblyInfo)data);
				return;
			case DecompilationType.TypeMethods:
				DecompileTypeMethods((DecompileTypeMethods)data);
				return;
			case DecompilationType.WholeProject:
				ProjectExporter.Export(langSettings.Settings, (DecompileWholeProject)data);
				return;
			}
			base.Decompile(decompilationType, data);
		}

		ILSpySettings CreateEditorSettings(bool useUsingDeclarations) {
			var settings = langSettings.Settings.Clone();
			settings.UsingDeclarations = useUsingDeclarations;
			settings.AlwaysUseGlobal = !useUsingDeclarations;
			return settings;
		}

		void DecompilePartial(DecompilePartialType info) {
			var settings = CreateEditorSettings(info.UseUsingDeclarations);
			Run(info.Type.Module, info.Output, info.Context, settings, d => d.DecompileTypes(new[] { (SRM.TypeDefinitionHandle)ToHandle(info.Type) }),
				new DecompilePartialTransform(info.Type, info.Definitions, info.ShowDefinitions, info.AddPartialKeyword, info.InterfacesToRemove));
		}

		void DecompileAssemblyInfo(DecompileAssemblyInfo info) {
			var settings = langSettings.Settings.Clone();
			var transforms = info.KeepAllAttributes ? Array.Empty<IAstTransform>() : new IAstTransform[] { new AssemblyInfoTransform() };
			Run(info.Module, info.Output, info.Context, settings, d => d.DecompileModuleAndAssemblyAttributes(), transforms);
		}

		void DecompileTypeMethods(DecompileTypeMethods info) {
			var settings = CreateEditorSettings(!info.DecompileHidden);
			Run(info.Type.Module, info.Output, info.Context, settings, d => d.DecompileTypes(new[] { (SRM.TypeDefinitionHandle)ToHandle(info.Type) }),
				new DecompileTypeMethodsTransform(info.Types, info.Methods, !info.DecompileHidden, info.ShowAll));
		}
		#endregion
	}

	static class ILSpySettingsExtensions {
		/// <summary>The modern engine has no "show all members" option; NetSpy keeps it off</summary>
		public static bool ForceShowAllMembers(this ILSpySettings settings) => false;
	}

	/// <summary>
	/// XML documentation from the .xml files dnSpy finds next to the assemblies (and the reference assemblies)
	/// </summary>
	sealed class DnSpyDocumentationProvider : IDocumentationProvider {
		public string? GetDocumentation(IEntity entity) {
			var module = DnlibMetadata.TryGetModule(entity.ParentModule?.MetadataFile);
			if (module is null)
				return null;
			var provider = XmlDocLoader.LoadDocumentation(module);
			if (provider is null)
				return null;
			return provider.GetDocumentation(entity.GetIdString());
		}
	}
}
