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
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;
using NetSpy.Decompiler.DebugInfo;
using NetSpy.Decompiler.Metadata;
using NetSpy.Decompiler.TypeSystem;
using SRM = System.Reflection.Metadata;

namespace NetSpy.Decompiler.Engine.Core.CSharp {
	/// <summary>
	/// Maps NetSpy's dnlib modules to the <see cref="MetadataFile"/>s the modern NetSpy engine reads.
	/// Both views use the same metadata tokens, which is what lets NetSpy convert every NetSpy symbol
	/// back to the dnlib object used for references, colors and debug info.
	/// </summary>
	static class DnlibMetadata {
		sealed class Entry {
			public readonly MetadataFile File;
			public readonly int ModuleVersion;
			public Entry(MetadataFile file, int moduleVersion) {
				File = file;
				ModuleVersion = moduleVersion;
			}
		}

		static readonly ConditionalWeakTable<ModuleDef, Entry> moduleToFile = new ConditionalWeakTable<ModuleDef, Entry>();
		static readonly ConditionalWeakTable<MetadataFile, ModuleDef> fileToModule = new ConditionalWeakTable<MetadataFile, ModuleDef>();
		static readonly ConditionalWeakTable<ModuleDef, StrongBox<int>> moduleVersions = new ConditionalWeakTable<ModuleDef, StrongBox<int>>();
		static readonly object lockObj = new object();

		/// <summary>
		/// Called when a module was modified in memory (eg. by the assembly editor) so the next
		/// decompilation re-serializes it instead of reusing the cached original bytes.
		/// </summary>
		public static void InvalidateModule(ModuleDef module) {
			lock (lockObj) {
				moduleVersions.GetOrCreateValue(module).Value++;
				TypeSystemCache.Invalidate(module);
			}
		}

		static int GetModuleVersion(ModuleDef module) {
			lock (lockObj)
				return moduleVersions.TryGetValue(module, out var box) ? box.Value : 0;
		}

		public static bool IsModified(ModuleDef module) => GetModuleVersion(module) != 0;

		public static MetadataFile GetMetadataFile(ModuleDef module) {
			int version = GetModuleVersion(module);
			lock (lockObj) {
				if (moduleToFile.TryGetValue(module, out var entry) && entry.ModuleVersion == version)
					return entry.File;
			}

			var file = CreateMetadataFile(module, version != 0);
			lock (lockObj) {
				if (moduleToFile.TryGetValue(module, out var entry)) {
					if (entry.ModuleVersion == version)
						return entry.File;
					moduleToFile.Remove(module);
				}
				moduleToFile.Add(module, new Entry(file, version));
				fileToModule.Add(file, module);
			}
			return file;
		}

		public static ModuleDef? TryGetModule(MetadataFile? file) {
			if (file is null)
				return null;
			lock (lockObj)
				return fileToModule.TryGetValue(file, out var module) ? module : null;
		}

		static MetadataFile CreateMetadataFile(ModuleDef module, bool forceWrite) {
			string fileName = !string.IsNullOrEmpty(module.Location) ? module.Location : (module.Name?.String ?? "module.dll");
			if (!forceWrite && module is ModuleDefMD md) {
				// Reuse the bytes dnlib already has in memory: same tokens, no disk access
				// Modules read from process memory (debugger) use the loaded (memory) layout
				var peImage = md.Metadata.PEImage;
				var reader = peImage.CreateReader();
				var bytes = reader.ReadBytes((int)reader.Length);
				var options = PEStreamOptions.PrefetchEntireImage;
				if (!peImage.IsFileImageLayout)
					options |= PEStreamOptions.IsLoadedImage;
				return new PEFile(fileName, new PEReader(new MemoryStream(bytes), options), MetadataReaderOptions.ApplyWindowsRuntimeProjections);
			}

			// Created or modified in memory: write it with the original tokens preserved
			var stream = new MemoryStream();
			if (module is ModuleDefMD) {
				var opts = new ModuleWriterOptions(module);
				opts.MetadataOptions.Flags |= MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack;
				opts.Logger = DummyLogger.NoThrowInstance;
				module.Write(stream, opts);
			}
			else {
				var opts = new ModuleWriterOptions(module);
				opts.MetadataOptions.Flags |= MetadataFlags.PreserveRids | MetadataFlags.KeepOldMaxStack;
				opts.Logger = DummyLogger.NoThrowInstance;
				module.Write(stream, opts);
			}
			stream.Position = 0;
			return new PEFile(fileName, stream, PEStreamOptions.PrefetchEntireImage, MetadataReaderOptions.ApplyWindowsRuntimeProjections);
		}

		/// <summary>
		/// Resolves a metadata token of <paramref name="module"/> to its dnlib definition
		/// </summary>
		public static object? ResolveToken(ModuleDef module, EntityHandle handle) {
			if (handle.IsNil)
				return null;
			uint token = (uint)MetadataTokens.GetToken(handle);
			if (module is ModuleDefMD md)
				return md.ResolveToken(token);
			return TokenMaps.Get(module).TryGetValue(token, out var obj) ? obj : null;
		}

		/// <summary>
		/// Token map for modules that aren't backed by metadata (created in memory)
		/// </summary>
		static class TokenMaps {
			static readonly ConditionalWeakTable<ModuleDef, Dictionary<uint, object>> maps = new ConditionalWeakTable<ModuleDef, Dictionary<uint, object>>();

			public static Dictionary<uint, object> Get(ModuleDef module) {
				lock (maps) {
					if (maps.TryGetValue(module, out var dict))
						return dict;
					dict = new Dictionary<uint, object>();
					foreach (var type in module.GetTypes()) {
						dict[type.MDToken.Raw] = type;
						foreach (var f in type.Fields)
							dict[f.MDToken.Raw] = f;
						foreach (var m in type.Methods)
							dict[m.MDToken.Raw] = m;
						foreach (var p in type.Properties)
							dict[p.MDToken.Raw] = p;
						foreach (var e in type.Events)
							dict[e.MDToken.Raw] = e;
					}
					maps.Add(module, dict);
					return dict;
				}
			}
		}
	}

	/// <summary>
	/// NetSpy assembly resolver that asks NetSpy's own resolver, so NetSpy sees exactly the
	/// assemblies that are loaded in NetSpy (and honors DecompilationContext.DisableAssemblyLoad)
	/// </summary>
	sealed class DnlibAssemblyResolver : NetSpy.Decompiler.Metadata.IAssemblyResolver {
		readonly ModuleDef mainModule;

		public DnlibAssemblyResolver(ModuleDef mainModule) => this.mainModule = mainModule;

		public MetadataFile? Resolve(IAssemblyReference reference) {
			var asmRef = new AssemblyRefUser(reference.Name, reference.Version ?? new Version(0, 0, 0, 0),
				reference.PublicKeyToken is byte[] pkt && pkt.Length != 0 ? new PublicKeyToken(pkt) : null,
				reference.Culture ?? string.Empty);
			if (reference.IsRetargetable)
				asmRef.Attributes |= AssemblyAttributes.Retargetable;
			if (reference.IsWindowsRuntime)
				asmRef.Attributes |= AssemblyAttributes.ContentType_WindowsRuntime;
			AssemblyDef? asm;
			try {
				asm = mainModule.Context.AssemblyResolver.Resolve(asmRef, mainModule);
			}
			catch (Exception) {
				asm = null;
			}
			var module = asm?.ManifestModule ?? ResolveWithFallback(reference);
			if (debugResolve)
				Console.Error.WriteLine($"[NetSpy resolve] {reference.FullName} => {(module is null ? "NOT FOUND" : module.Location)}");
			return module is null ? null : DnlibMetadata.GetMetadataFile(module);
		}

		/// <summary>
		/// dnlib's plain resolver (used by NetSpy.Console) only searches the GAC and the given paths, so it
		/// can't find .NET Core / .NET 5+ framework assemblies. NetSpy's resolver knows the installed
		/// shared frameworks and reference packs; the file it finds is loaded with dnlib so it still maps
		/// to dnlib objects (references, colors).
		/// </summary>
		ModuleDef? ResolveWithFallback(IAssemblyReference reference) {
			try {
				fallbackResolver ??= new UniversalAssemblyResolver(mainModule.Location, false,
					DnlibMetadata.GetMetadataFile(mainModule).DetectTargetFrameworkId());
				var path = fallbackResolver.FindAssemblyFile(reference);
				if (path is null)
					return null;
				lock (fallbackModules) {
					if (!fallbackModules.TryGetValue(path, out var module)) {
						module = ModuleDefMD.Load(path, mainModule.Context);
						fallbackModules.Add(path, module);
						(mainModule.Context.AssemblyResolver as AssemblyResolver)?.AddToCache(module);
					}
					return module;
				}
			}
			catch (Exception) {
				return null;
			}
		}
		UniversalAssemblyResolver? fallbackResolver;
		static readonly Dictionary<string, ModuleDef> fallbackModules = new Dictionary<string, ModuleDef>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Set NETSPY_DEBUG_RESOLVE=1 to log every assembly the NetSpy engine asks for</summary>
		static readonly bool debugResolve = Environment.GetEnvironmentVariable("NETSPY_DEBUG_RESOLVE") == "1";

		public MetadataFile? ResolveModule(MetadataFile mainModuleFile, string moduleName) {
			var owner = DnlibMetadata.TryGetModule(mainModuleFile) ?? mainModule;
			var asm = owner.Assembly;
			if (asm is not null) {
				foreach (var m in asm.Modules) {
					if (StringComparer.OrdinalIgnoreCase.Equals(m.Name?.String, moduleName))
						return DnlibMetadata.GetMetadataFile(m);
				}
			}
			return null;
		}

		public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));
		public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));
		public IDisposable? BeginSnapshot() => null;
	}

	/// <summary>
	/// Caches the (expensive) NetSpy type system of a module, one per type system option set
	/// </summary>
	static class TypeSystemCache {
		static readonly ConditionalWeakTable<ModuleDef, Dictionary<TypeSystemOptions, DecompilerTypeSystem>> cache = new ConditionalWeakTable<ModuleDef, Dictionary<TypeSystemOptions, DecompilerTypeSystem>>();

		public static DecompilerTypeSystem Get(ModuleDef module, NetSpy.Decompiler.DecompilerSettings settings) {
			var options = DecompilerTypeSystem.GetOptions(settings);
			var file = DnlibMetadata.GetMetadataFile(module);
			lock (cache) {
				var dict = cache.GetOrCreateValue(module);
				if (dict.TryGetValue(options, out var ts) && ts.MainModule.MetadataFile == file)
					return ts;
			}
			var newTs = new DecompilerTypeSystem(file, new DnlibAssemblyResolver(module), options);
			lock (cache) {
				var dict = cache.GetOrCreateValue(module);
				dict[options] = newTs;
			}
			return newTs;
		}

		public static void Invalidate(ModuleDef module) {
			lock (cache)
				cache.Remove(module);
		}
	}

	/// <summary>
	/// Gives NetSpy the local variable names (and tuple names / dynamic flags) from the PDB
	/// that dnlib has already loaded
	/// </summary>
	sealed class DnlibDebugInfoProvider : IDebugInfoProvider, ISourceImportProvider {
		readonly ModuleDef module;

		public DnlibDebugInfoProvider(ModuleDef module) => this.module = module;

		public string Description => "dnlib PDB reader";
		public string SourceFileName => module.Location ?? string.Empty;

		MethodDef? GetMethod(MethodDefinitionHandle handle) =>
			DnlibMetadata.ResolveToken(module, handle) as MethodDef;

		// The source positions of the method's statements (used to lay out the decompiled code like the original)
		public IList<NetSpy.Decompiler.DebugInfo.SequencePoint> GetSequencePoints(MethodDefinitionHandle method) {
			var body = GetMethod(method)?.Body;
			if (body is null || body.Instructions.Count == 0)
				return Array.Empty<NetSpy.Decompiler.DebugInfo.SequencePoint>();
			var result = new List<NetSpy.Decompiler.DebugInfo.SequencePoint>();
			var instructions = body.Instructions;
			var last = instructions[instructions.Count - 1];
			int codeSize = (int)last.Offset + last.GetSize();
			foreach (var instruction in instructions) {
				if (instruction.SequencePoint is not { } sp)
					continue;
				if (result.Count > 0)
					result[result.Count - 1].EndOffset = (int)instruction.Offset;
				result.Add(new NetSpy.Decompiler.DebugInfo.SequencePoint {
					Offset = (int)instruction.Offset,
					EndOffset = codeSize,
					StartLine = sp.StartLine,
					StartColumn = sp.StartColumn,
					EndLine = sp.EndLine,
					EndColumn = sp.EndColumn,
					DocumentUrl = sp.Document?.Url ?? string.Empty,
				});
			}
			return result;
		}

		public IList<Variable> GetVariables(MethodDefinitionHandle method) {
			var list = new List<Variable>();
			var md = GetMethod(method);
			if (md?.Body is { PdbMethod.Scope: PdbScope scope } body && body.Instructions.Count > 0) {
				var last = body.Instructions[body.Instructions.Count - 1];
				AddVariables(scope, list, (int)last.Offset + last.GetSize());
			}
			return list;
		}

		static void AddVariables(PdbScope scope, List<Variable> list, int codeSize) {
			int start = scope.Start is { } s ? (int)s.Offset : 0;
			int end = scope.End is { } e ? (int)e.Offset : codeSize;
			foreach (var local in scope.Variables)
				list.Add(new Variable(local.Index, local.Name) { ScopeStart = start, ScopeEnd = end });
			foreach (var child in scope.Scopes)
				AddVariables(child, list, codeSize);
		}

		public bool TryGetName(MethodDefinitionHandle method, int index, out string name) {
			var md = GetMethod(method);
			if (md?.Body?.PdbMethod?.Scope is PdbScope scope && TryGetName(scope, index, out var n)) {
				name = n;
				return true;
			}
			name = null!;
			return false;
		}

		static bool TryGetName(PdbScope scope, int index, out string name) {
			foreach (var local in scope.Variables) {
				if (local.Index == index && !string.IsNullOrEmpty(local.Name)) {
					name = local.Name;
					return true;
				}
			}
			foreach (var child in scope.Scopes) {
				if (TryGetName(child, index, out name))
					return true;
			}
			name = null!;
			return false;
		}

		// The source files and using directives of the original project (NetSpy: exported like the original)
		public IReadOnlyList<string> SourceDocuments =>
			module.PdbState?.Documents.Select(d => d.Url).Where(u => !string.IsNullOrEmpty(u)).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

		readonly Dictionary<PdbImportScope, int> scopeIds = new Dictionary<PdbImportScope, int>();

		public (int ScopeId, IReadOnlyList<SourceImport> Imports)? GetFileImports(MethodDefinitionHandle method) {
			var scope = GetMethod(method)?.Body?.PdbMethod?.Scope?.ImportScope;
			if (scope is null)
				return null;
			// the file's scope is the one right below the compilation's root scope
			while (scope.Parent is { Parent: not null } parent)
				scope = parent;
			if (scope.Parent is null)
				return null;
			int id;
			lock (scopeIds) {
				if (!scopeIds.TryGetValue(scope, out id))
					scopeIds.Add(scope, id = scopeIds.Count + 1);
			}
			var imports = new List<SourceImport>();
			foreach (var import in scope.Imports) {
				switch (import) {
				case PdbImportNamespace ns:
					imports.Add(new SourceImport { Kind = SourceImportKind.Namespace, Namespace = ns.TargetNamespace });
					break;
				case PdbAliasNamespace aliasNs:
					imports.Add(new SourceImport { Kind = SourceImportKind.NamespaceAlias, Alias = aliasNs.Alias, Namespace = aliasNs.TargetNamespace });
					break;
				case PdbImportType type when type.TargetType is not null:
					imports.Add(new SourceImport { Kind = SourceImportKind.Type, Type = MetadataTokens.EntityHandle((int)type.TargetType.MDToken.Raw) });
					break;
				case PdbAliasType aliasType when aliasType.TargetType is not null:
					imports.Add(new SourceImport { Kind = SourceImportKind.TypeAlias, Alias = aliasType.Alias, Type = MetadataTokens.EntityHandle((int)aliasType.TargetType.MDToken.Raw) });
					break;
				}
			}
			return (id, imports);
		}

		public bool TryGetExtraTypeInfo(MethodDefinitionHandle method, int index, out PdbExtraTypeInfo extraTypeInfo) {
			extraTypeInfo = default;
			var md = GetMethod(method);
			var body = md?.Body;
			if (body is null)
				return false;
			bool found = false;
			foreach (var cdi in md!.CustomDebugInfos) {
				if (cdi is PdbDynamicLocalsCustomDebugInfo dynInfo) {
					foreach (var dl in dynInfo.Locals) {
						if (dl.Local is dnlib.DotNet.Emit.Local l && l.Index == index) {
							extraTypeInfo.DynamicFlags = dl.Flags.Select(a => a != 0).ToArray();
							found = true;
						}
					}
				}
			}
			if (body.PdbMethod?.Scope is PdbScope scope && TryGetTupleNames(scope, index, out var names)) {
				extraTypeInfo.TupleElementNames = names;
				found = true;
			}
			// NetSpy: a portable PDB keeps the dynamic flags on the local itself
			if (extraTypeInfo.DynamicFlags is null && body.PdbMethod?.Scope is PdbScope localScope && TryGetDynamicFlags(localScope, index, out var flags)) {
				extraTypeInfo.DynamicFlags = flags;
				found = true;
			}
			return found;
		}

		static bool TryGetDynamicFlags(PdbScope scope, int index, out bool[] flags) {
			foreach (var local in scope.Variables) {
				if (local.Index != index)
					continue;
				foreach (var cdi in local.CustomDebugInfos) {
					if (cdi is PdbDynamicLocalVariablesCustomDebugInfo dynamicInfo) {
						flags = dynamicInfo.Flags;
						return true;
					}
				}
			}
			foreach (var child in scope.Scopes) {
				if (TryGetDynamicFlags(child, index, out flags))
					return true;
			}
			flags = null!;
			return false;
		}

		static bool TryGetTupleNames(PdbScope scope, int index, out string[] names) {
			foreach (var local in scope.Variables) {
				if (local.Index != index)
					continue;
				foreach (var cdi in local.CustomDebugInfos) {
					if (cdi is PortablePdbTupleElementNamesCustomDebugInfo tuple) {
						names = tuple.Names.Select(a => a ?? string.Empty).ToArray();
						return true;
					}
				}
			}
			foreach (var child in scope.Scopes) {
				if (TryGetTupleNames(child, index, out names))
					return true;
			}
			names = null!;
			return false;
		}
	}
}
