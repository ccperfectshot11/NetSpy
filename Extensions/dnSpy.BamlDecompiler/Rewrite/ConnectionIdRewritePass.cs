/*
	Copyright (c) 2015 Ki
	Copyright (c) 2026 NetSpy contributors (ported to the modern ILSpy ILAst, based on
	ICSharpCode.BamlDecompiler/Rewrite/ConnectionIdRewritePass.cs from ILSpy)

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
	THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;
using dnSpy.BamlDecompiler.Baml;
using dnSpy.BamlDecompiler.Properties;
using dnSpy.BamlDecompiler.Xaml;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.CSharp;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;
using ILSpyDecompiler = ICSharpCode.Decompiler.CSharp.CSharpDecompiler;
using ILSpyField = ICSharpCode.Decompiler.TypeSystem.IField;
using ILSpySettings = ICSharpCode.Decompiler.DecompilerSettings;

namespace dnSpy.BamlDecompiler.Rewrite {
	/// <summary>
	/// Adds x:Name / x:FieldModifier / event handler attributes by analyzing the IComponentConnector.Connect
	/// and IStyleConnector.Connect methods that the XAML compiler generates
	/// </summary>
	sealed class ConnectionIdRewritePass : IRewritePass {
		static bool Impl(MethodDef method, MethodDef ifaceMethod) {
			if (method.HasOverrides) {
				var comparer = new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable);
				if (method.Overrides.Any(m => comparer.Equals(m.MethodDeclaration, ifaceMethod)))
					return true;
			}

			if (method.Name != ifaceMethod.Name)
				return false;
			return TypesHierarchyHelpers.MatchInterfaceMethod(method, ifaceMethod, ifaceMethod.DeclaringType);
		}

		public void Run(XamlContext ctx, XDocument document) {
			var xClass = document.Root.Elements().First().Attribute(ctx.GetKnownNamespace("Class", XamlContext.KnownNamespace_Xaml));
			if (xClass is null)
				return;

			var type = ctx.Module.Find(xClass.Value, true);
			if (type is null)
				return;

			var componentConnectorConnect = ctx.Baml.KnownThings.Types(KnownTypes.IComponentConnector).TypeDef.FindMethod("Connect");
			var styleConnectorConnect = ctx.Baml.KnownThings.Types(KnownTypes.IStyleConnector).TypeDef.FindMethod("Connect");

			var connIds = new Dictionary<int, Action<XamlContext, XElement>>();

			if (!CollectConnectionIds(ctx, componentConnectorConnect, type, connIds)) {
				var msg = dnSpy_BamlDecompiler_Resources.Error_IComponentConnectorConnectCannotBeParsed;
				document.Root.AddBeforeSelf(new XComment(string.Format(msg, type.ReflectionFullName)));
			}

			if (!CollectConnectionIds(ctx, styleConnectorConnect, type, connIds)) {
				var msg = dnSpy_BamlDecompiler_Resources.Error_IStyleConnectorConnectCannotBeParsed;
				document.Root.AddBeforeSelf(new XComment(string.Format(msg, type.ReflectionFullName)));
			}

			foreach (var elem in document.Elements()) {
				ProcessElement(ctx, elem, connIds);
			}
		}

		bool CollectConnectionIds(XamlContext ctx, MethodDef connectInterfaceMethod, TypeDef currentType, Dictionary<int, Action<XamlContext, XElement>> allConnIds) {
			MethodDef connect = null;
			foreach (var method in currentType.Methods) {
				if (Impl(method, connectInterfaceMethod)) {
					connect = method;
					break;
				}
			}

			if (connect is not null) {
				Dictionary<int, Action<XamlContext, XElement>> connIds = null;
				try {
					connIds = ExtractConnectionId(ctx, connect);
				}
				catch {
				}

				if (connIds is null)
					return false;

				foreach (var keyValuePair in connIds)
					allConnIds[keyValuePair.Key] = keyValuePair.Value;
			}

			return true;
		}

		static void ProcessElement(XamlContext ctx, XElement elem, Dictionary<int, Action<XamlContext, XElement>> connIds) {
			CheckConnectionId(ctx, elem, connIds);
			foreach (var child in elem.Elements()) {
				ProcessElement(ctx, child, connIds);
			}
		}

		static void CheckConnectionId(XamlContext ctx, XElement elem, Dictionary<int, Action<XamlContext, XElement>> connIds) {
			foreach (var connId in elem.Annotations<BamlConnectionId>()) {
				if (!connIds.TryGetValue((int)connId.Id, out var cb)) {
					elem.AddBeforeSelf(new XComment(string.Format(dnSpy_BamlDecompiler_Resources.Error_UnknownConnectionId, connId.Id)));
					return;
				}

				cb(ctx, elem);
			}
		}

		sealed class FieldAssignment {
			public string FieldName;
			public bool IsPublic;

			public void Callback(XamlContext ctx, XElement elem) {
				var xName = ctx.GetKnownNamespace("Name", XamlContext.KnownNamespace_Xaml, elem);
				if (elem.Attribute("Name") is null && elem.Attribute(xName) is null)
					elem.Add(new XAttribute(xName, IdentifierEscaper.Escape(FieldName)));

				if (IsPublic) {
					var fieldModifier = ctx.GetKnownNamespace("FieldModifier", XamlContext.KnownNamespace_Xaml, elem);
					elem.Add(new XAttribute(fieldModifier, ctx.BamlDecompilerOptions.PublicFieldModifier));
				}
			}
		}

		sealed class EventAttachment {
			public ITypeDefOrRef AttachedType;
			public string EventName;
			public string MethodName;

			public void Callback(XamlContext ctx, XElement elem) {
				var type = elem.Annotation<XamlType>();
				if (type is not null && type.TypeNamespace == "System.Windows" && type.TypeName == "Style") {
					elem.Add(new XElement(type.Namespace + "EventSetter",
						new XAttribute("Event", IdentifierEscaper.Escape(EventName)),
						new XAttribute("Handler", IdentifierEscaper.Escape(MethodName))));
					return;
				}

				string encodedEventName = XmlConvert.EncodeName(EventName);
				XName name;
				if (AttachedType is not null) {
					var clrNs = AttachedType.ReflectionNamespace;
					var xmlNs = ctx.XmlNs.LookupXmlns(AttachedType.DefinitionAssembly, clrNs);
					var xmlNamespace = ctx.GetXmlNamespace(xmlNs);
					if (xmlNamespace is not null)
						name = xmlNamespace.GetName(encodedEventName);
					else
						name = $"{XmlConvert.EncodeName(AttachedType.Name)}.{encodedEventName}";
				}
				else
					name = encodedEventName;

				elem.Add(new XAttribute(name, IdentifierEscaper.Escape(MethodName)));
			}
		}

		/// <summary>
		/// Decompiles Connect() to the modern ILSpy ILAst and finds, for every connection id, the
		/// field it's assigned to and the event handlers it attaches
		/// </summary>
		static Dictionary<int, Action<XamlContext, XElement>> ExtractConnectionId(XamlContext ctx, MethodDef method) {
			var module = method.Module;
			var settings = new ILSpySettings();
			var typeSystem = TypeSystemCache.Get(module, settings);
			var converter = new SymbolConverter(module);
			var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle((int)method.MDToken.Raw);
			var file = typeSystem.MainModule.MetadataFile;
			var md = file.Metadata.GetMethodDefinition(handle);
			if (md.RelativeVirtualAddress <= 0)
				return null;
			var connectMethod = typeSystem.MainModule.GetDefinition(handle);
			var body = file.GetMethodBody(md.RelativeVirtualAddress);
			var genericContext = new GenericContext(connectMethod.DeclaringType?.TypeParameters, connectMethod.TypeParameters);

			var function = new ILReader(typeSystem.MainModule).ReadIL(handle, body, genericContext, ILFunctionKind.TopLevelFunction, ctx.CancellationToken);
			var context = new ILTransformContext(function, typeSystem, null, settings) {
				CancellationToken = ctx.CancellationToken,
			};
			function.RunTransforms(ILSpyDecompiler.GetILTransforms(), context);

			var connIds = new Dictionary<int, Action<XamlContext, XElement>>();
			var block = function.Body.Children.OfType<Block>().FirstOrDefault();
			var ilSwitch = block?.Descendants.OfType<SwitchInstruction>().FirstOrDefault() ?? function.Descendants.OfType<SwitchInstruction>().FirstOrDefault();
			if (ilSwitch is not null) {
				foreach (var section in ilSwitch.Sections) {
					if (section.Labels.Count() > 1000)
						continue;
					Add(section.Labels.Values.Select(a => (int)a), section.Body);
				}
			}
			else {
				foreach (var ifInst in function.Descendants.OfType<IfInstruction>()) {
					if (ifInst.Condition is not Comp comp)
						continue;
					if (comp.Kind != ComparisonKind.Inequality && comp.Kind != ComparisonKind.Equality)
						continue;
					if (!comp.Right.MatchLdcI4(out int id))
						continue;
					var inst = comp.Kind == ComparisonKind.Inequality ? ifInst.FalseInst : ifInst.TrueInst;
					Add(new[] { id }, inst);
				}
			}
			return connIds;

			void Add(IEnumerable<int> ids, ILInstruction inst) {
				Action<XamlContext, XElement> cb = null;
				var field = FindField(inst);
				if (field is not null) {
					var fieldDef = converter.GetReference(field) as FieldDef;
					cb += new FieldAssignment { FieldName = field.Name, IsPublic = fieldDef?.IsPublic ?? field.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public }.Callback;
				}
				var events = new List<EventAttachment>();
				FindEvents(inst, events, converter);
				foreach (var ev in events)
					cb += ev.Callback;
				if (cb is null)
					return;
				foreach (var id in ids)
					connIds[id] = cb;
			}
		}

		static ILSpyField FindField(ILInstruction inst) {
			switch (inst) {
			case Block b:
				var first = b.Instructions.FirstOrDefault();
				return first is not null && MatchFieldAssignment(first, out var field) ? field : null;
			case Branch br:
				return FindField(br.TargetBlock);
			default:
				return MatchFieldAssignment(inst, out field) ? field : null;
			}
		}

		// stfld field(ldloc this, castclass T(ldloc target))
		static bool MatchFieldAssignment(ILInstruction inst, out ILSpyField field) {
			field = null;
			if (!inst.MatchStFld(out _, out var fld, out var value) || !value.MatchCastClass(out var arg, out _)
				|| !(arg.MatchLdLoc(out var t) && t.Kind == VariableKind.Parameter && t.Index == 1))
				return false;
			field = fld;
			return true;
		}

		static void FindEvents(ILInstruction inst, List<EventAttachment> events, SymbolConverter converter) {
			switch (inst) {
			case Block b:
				for (int i = 0; i < b.Instructions.Count;) {
					if (MatchEventSetterCreation(b, ref i, out var setterEvent))
						events.Add(setterEvent);
					else
						i++;
				}
				foreach (var node in b.Instructions) {
					if (MatchSimpleEventRegistration(node, converter, out var ev))
						events.Add(ev);
				}
				break;
			case Branch br:
				FindEvents(br.TargetBlock, events, converter);
				break;
			default:
				if (MatchSimpleEventRegistration(inst, converter, out var simpleEvent))
					events.Add(simpleEvent);
				break;
			}
		}

		// stloc v(newobj EventSetter..ctor())
		// callvirt set_Event(ldloc v, ldsfld eventName)
		// callvirt set_Handler(ldloc v, newobj RoutedEventHandler..ctor(ldloc this, ldftn eventHandler))
		// callvirt Add(callvirt get_Setters(castclass System.Windows.Style(ldloc target)), ldloc v)
		static bool MatchEventSetterCreation(Block b, ref int pos, out EventAttachment @event) {
			@event = null;
			var instr = b.Instructions;
			if (pos + 3 >= instr.Count)
				return false;
			if (!instr[pos + 0].MatchStLoc(out var v, out var initializer))
				return false;
			if (!(initializer is NewObj newObj && newObj.Method.DeclaringType.FullName == "System.Windows.EventSetter" && newObj.Arguments.Count == 0))
				return false;
			if (!(instr[pos + 1] is CallVirt setEventCall && setEventCall.Arguments.Count == 2))
				return false;
			if (!setEventCall.Method.IsAccessor || setEventCall.Method.Name != "set_Event" || !setEventCall.Arguments[0].MatchLdLoc(v))
				return false;
			if (!setEventCall.Arguments[1].MatchLdsFld(out var eventField))
				return false;
			if (!(instr[pos + 2] is CallVirt setHandlerCall && setHandlerCall.Arguments.Count == 2))
				return false;
			if (!setHandlerCall.Method.IsAccessor || setHandlerCall.Method.Name != "set_Handler" || !setHandlerCall.Arguments[0].MatchLdLoc(v))
				return false;
			if (!MatchEventHandlerCreation(setHandlerCall.Arguments[1], out string handlerName))
				return false;
			if (!(instr[pos + 3] is CallVirt addCall && addCall.Arguments.Count == 2) || addCall.Method.Name != "Add")
				return false;
			if (!(addCall.Arguments[0] is CallVirt getSettersCall && getSettersCall.Arguments.Count == 1))
				return false;
			if (!getSettersCall.Method.IsAccessor || getSettersCall.Method.Name != "get_Setters")
				return false;
			if (!getSettersCall.Arguments[0].MatchCastClass(out var arg, out var type) || type.FullName != "System.Windows.Style")
				return false;
			if (!(arg.MatchLdLoc(out var t) && t.Kind == VariableKind.Parameter && t.Index == 1))
				return false;
			if (!addCall.Arguments[1].MatchLdLoc(v))
				return false;

			@event = new EventAttachment { EventName = RemoveEventSuffix(eventField.Name), MethodName = handlerName };
			pos += 4;
			return true;
		}

		static bool MatchSimpleEventRegistration(ILInstruction inst, SymbolConverter converter, out EventAttachment @event) {
			@event = null;
			if (inst is not CallInstruction call || call.OpCode == OpCode.NewObj)
				return false;

			// Attached event: UIElement.AddHandler(ldsfld XxxEvent, newobj Handler(this, ldftn method))
			if (call.Arguments.Count == 3 && call.Method.Name == "AddHandler" && call.Method.Parameters.Count == 2) {
				if (!call.Arguments[1].MatchLdsFld(out var field))
					return false;
				if (!MatchEventHandlerCreation(call.Arguments[2], out string handlerName))
					return false;
				var attachedType = converter.GetReference(field.DeclaringType) as ITypeDefOrRef;
				@event = new EventAttachment { AttachedType = attachedType, EventName = RemoveEventSuffix(field.Name), MethodName = handlerName };
				return true;
			}

			// CLR event: add_Xxx(newobj Handler(this, ldftn method))
			if (call.Arguments.Count == 2 && call.Method.Name.StartsWith("add_", StringComparison.Ordinal) && call.Method.Parameters.Count == 1) {
				if (!MatchEventHandlerCreation(call.Arguments[1], out string handlerName))
					return false;
				@event = new EventAttachment { EventName = call.Method.Name.Substring("add_".Length), MethodName = handlerName };
				return true;
			}
			return false;
		}

		static bool MatchEventHandlerCreation(ILInstruction inst, out string handlerName) {
			handlerName = "";
			if (inst is not NewObj newObj || newObj.Arguments.Count != 2)
				return false;
			var ldftn = newObj.Arguments[1];
			if (ldftn.OpCode != OpCode.LdFtn && ldftn.OpCode != OpCode.LdVirtFtn)
				return false;
			handlerName = ((IInstructionWithMethodOperand)ldftn).Method.Name;
			return true;
		}

		static string RemoveEventSuffix(string name) {
			if (name.EndsWith("Event", StringComparison.Ordinal) && name.Length > "Event".Length)
				return name.Substring(0, name.Length - "Event".Length);
			return name;
		}
	}
}
