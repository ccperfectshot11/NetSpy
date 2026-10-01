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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

using NetSpy.Decompiler.DebugInfo;
using NetSpy.Decompiler.Disassembler;
using NetSpy.Decompiler.Metadata;

using SequencePoint = NetSpy.Decompiler.DebugInfo.SequencePoint;

namespace NetSpy.Decompiler.CSharp.ProjectDecompiler
{
	/// <summary>
	/// NetSpy: partial types declared in several source files (Form1.cs + Form1.Designer.cs): which members
	/// were in which file, from the PDB documents of the methods and from where fields are used.
	/// </summary>
	static class SourcePartialTypes
	{
		/// <summary>
		/// For each top-level type declared in several files: the file names of its other parts (not the main
		/// file) and the members of each part (the main part has all the other members).
		/// </summary>
		public static Dictionary<TypeDefinitionHandle, Dictionary<string, HashSet<EntityHandle>>> Compute(MetadataFile module,
			IDebugInfoProvider provider, Dictionary<TypeDefinitionHandle, (string FileName, int Line)> sourceFiles, string root)
		{
			var metadata = module.Metadata;
			var result = new Dictionary<TypeDefinitionHandle, Dictionary<string, HashSet<EntityHandle>>>();
			foreach (var pair in sourceFiles)
			{
				var type = metadata.GetTypeDefinition(pair.Key);
				var memberFiles = new Dictionary<EntityHandle, string>();
				var fieldUse = new Dictionary<FieldDefinitionHandle, Dictionary<string, int>>();
				foreach (var methodHandle in type.GetMethods())
				{
					var points = GetPoints(provider, methodHandle, root);
					if (points.Count == 0)
						continue;
					var method = metadata.GetMethodDefinition(methodHandle);
					bool isConstructor = metadata.GetString(method.Name) is ".ctor" or ".cctor";
					// a constructor's first points are the field initializers (maybe in another file); its body is last
					var file = isConstructor ? points[points.Count - 1].File
						: points.GroupBy(p => p.File, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
					memberFiles[methodHandle] = file;
					CountFieldUses(module, method, points, fieldUse);
				}
				// fields: where they are stored (field initializers, InitializeComponent...) and read
				foreach (var use in fieldUse)
					memberFiles[use.Key] = use.Value.OrderByDescending(u => u.Value).First().Key;
				foreach (var propertyHandle in type.GetProperties())
				{
					var accessors = metadata.GetPropertyDefinition(propertyHandle).GetAccessors();
					var file = FileOf(memberFiles, accessors.Getter) ?? FileOf(memberFiles, accessors.Setter)
						?? BackingFieldFile(metadata, type, memberFiles, "<" + metadata.GetString(metadata.GetPropertyDefinition(propertyHandle).Name) + ">k__BackingField");
					if (file != null)
						memberFiles[propertyHandle] = file;
				}
				foreach (var eventHandle in type.GetEvents())
				{
					var accessors = metadata.GetEventDefinition(eventHandle).GetAccessors();
					var file = FileOf(memberFiles, accessors.Adder) ?? FileOf(memberFiles, accessors.Remover)
						?? BackingFieldFile(metadata, type, memberFiles, metadata.GetString(metadata.GetEventDefinition(eventHandle).Name));
					if (file != null)
						memberFiles[eventHandle] = file;
				}
				foreach (var nested in type.GetNestedTypes())
				{
					var files = metadata.GetTypeDefinition(nested).GetMethods().Select(m => FileOf(memberFiles, m) ?? MajorityFile(provider, m, root)).OfType<string>().ToList();
					if (files.Count > 0)
						memberFiles[nested] = files.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
				}
				var parts = memberFiles.Where(m => !string.Equals(m.Value, pair.Value.FileName, StringComparison.OrdinalIgnoreCase))
					.GroupBy(m => m.Value, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(g => g.Key, g => new HashSet<EntityHandle>(g.Select(m => m.Key)), StringComparer.OrdinalIgnoreCase);
				// a part with nothing a user writes (only compiler-generated members) isn't a part
				foreach (var part in parts.Where(p => p.Value.All(h => IsCompilerGenerated(metadata, h))).ToList())
					parts.Remove(part.Key);
				if (parts.Count > 0)
					result[pair.Key] = parts;
			}
			return result;
		}

		sealed class Point
		{
			public int Offset;
			public int EndOffset;
			public string File = "";
		}

		/// <summary>The method's sequence points in project files (generated files don't make a part).</summary>
		static List<Point> GetPoints(IDebugInfoProvider provider, MethodDefinitionHandle method, string root)
		{
			IList<SequencePoint> sequencePoints;
			try
			{
				sequencePoints = provider.GetSequencePoints(method);
			}
			catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
			{
				return new List<Point>();
			}
			return sequencePoints.Where(sp => !sp.IsHidden && !SourceFileLayout.IsGenerated(sp.DocumentUrl)
					&& sp.DocumentUrl.StartsWith(root, StringComparison.OrdinalIgnoreCase))
				.Select(sp => new Point {
					Offset = sp.Offset,
					EndOffset = sp.EndOffset,
					File = sp.DocumentUrl.Substring(root.Length).TrimStart('\\', '/').Replace('/', Path.DirectorySeparatorChar)
				})
				.ToList();
		}

		static string? MajorityFile(IDebugInfoProvider provider, MethodDefinitionHandle method, string? root)
		{
			return root == null ? null : GetPoints(provider, method, root).GroupBy(p => p.File, StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
		}

		/// <summary>Stores of a field count twice as much as reads: fields live where they are initialized.</summary>
		static void CountFieldUses(MetadataFile module, MethodDefinition method, List<Point> points, Dictionary<FieldDefinitionHandle, Dictionary<string, int>> uses)
		{
			if (method.RelativeVirtualAddress == 0)
				return;
			BlobReader reader;
			try
			{
				reader = module.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
			}
			catch (BadImageFormatException)
			{
				return;
			}
			while (reader.RemainingBytes > 0)
			{
				int offset = reader.Offset;
				var opCode = reader.DecodeOpCode();
				int weight = opCode switch {
					ILOpCode.Stfld or ILOpCode.Stsfld => 2,
					ILOpCode.Ldfld or ILOpCode.Ldsfld or ILOpCode.Ldflda or ILOpCode.Ldsflda => 1,
					_ => 0
				};
				if (weight == 0)
				{
					reader.SkipOperand(opCode);
					continue;
				}
				var token = MetadataTokens.EntityHandle(reader.ReadInt32());
				if (token.Kind != HandleKind.FieldDefinition)
					continue;
				var point = points.LastOrDefault(p => p.Offset <= offset);
				if (point == null)
					continue;
				var field = (FieldDefinitionHandle)token;
				if (!uses.TryGetValue(field, out var files))
					uses[field] = files = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				files.TryGetValue(point.File, out int count);
				files[point.File] = count + weight;
			}
		}

		static string? FileOf(Dictionary<EntityHandle, string> memberFiles, MethodDefinitionHandle method)
			=> !method.IsNil && memberFiles.TryGetValue(method, out var file) ? file : null;

		static string? BackingFieldFile(MetadataReader metadata, TypeDefinition type, Dictionary<EntityHandle, string> memberFiles, string fieldName)
		{
			foreach (var field in type.GetFields())
			{
				if (metadata.GetString(metadata.GetFieldDefinition(field).Name) == fieldName && memberFiles.TryGetValue(field, out var file))
					return file;
			}
			return null;
		}

		static bool IsCompilerGenerated(MetadataReader metadata, EntityHandle handle)
		{
			string name = handle.Kind switch {
				HandleKind.MethodDefinition => metadata.GetString(metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
				HandleKind.FieldDefinition => metadata.GetString(metadata.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
				HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
				_ => ""
			};
			return name.StartsWith("<", StringComparison.Ordinal);
		}
	}
}
