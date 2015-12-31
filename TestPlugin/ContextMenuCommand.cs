// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under MIT X11 license (for details please see \doc\license.txt)

using System;
using System.Linq;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.TreeView;
using Microsoft.Win32;
using Mono.Cecil;
using ICSharpCode.Decompiler;
using System.IO;
using Mono.Cecil.Cil;
using Microsoft.Cci.Pdb;

namespace TestPlugin
{
	[ExportContextMenuEntryAttribute(Header = "_Save Pdb")]
	public class SaveAssembly : IContextMenuEntry
	{
		public bool IsVisible(TextViewContext context)
		{
			return context.SelectedTreeNodes != null && context.SelectedTreeNodes.All(n => n is AssemblyTreeNode);
		}

		public bool IsEnabled(TextViewContext context)
		{
			return context.SelectedTreeNodes != null && context.SelectedTreeNodes.Length == 1;
		}

		public void Execute(TextViewContext context)
		{
			if (context.SelectedTreeNodes == null)
				return;
			AssemblyTreeNode node = (AssemblyTreeNode)context.SelectedTreeNodes[0];

			SaveFileDialog dlg = new SaveFileDialog();
			dlg.FileName = Path.ChangeExtension(node.LoadedAssembly.FileName, ".pdb");
			dlg.Filter = "PDB|*.pdb";
			if (false == dlg.ShowDialog(MainWindow.Instance))
				return;
			var pdbPath = dlg.FileName;
			dlg.FileName = Path.ChangeExtension(node.LoadedAssembly.FileName, node.Language.FileExtension);
			dlg.Filter = $"Source|*{node.Language.FileExtension}";
			if (false == dlg.ShowDialog(MainWindow.Instance))
				return;
			var sourcePath = dlg.FileName;

			DecompilationOptions options = new DecompilationOptions();
			options.FullDecompilation = true;

			SymbolOutput symbolOutput;
			using (var fs = File.Create(sourcePath))
			using (var sw = new StreamWriter(fs))
			{
				symbolOutput = new SymbolOutput(sw);
				node.Decompile(node.Language, symbolOutput, options);
			}

			var sourceDocument = new Document(sourcePath)
			{
				LanguageVendor = DocumentLanguageVendor.Microsoft,
				Type = DocumentType.Text
			};
			if (node.Language.Name == "C#")
				sourceDocument.Language = DocumentLanguage.CSharp;
			else if (node.Language.Name == "IL")
				sourceDocument.Language = DocumentLanguage.Cil;

			ModuleDefinition m;
			using (var fsAsm = File.OpenRead(node.LoadedAssembly.FileName))
				m = ModuleDefinition.ReadModule(fsAsm);
			using (var writer = new Mono.Cecil.Pdb.PdbWriterProvider().GetSymbolWriter(m, pdbPath))
			{
				foreach (var x in symbolOutput.Symbols)
				{
					if (false == x.CecilMethod.HasBody)
						continue;

					foreach (var i in x.CecilMethod.Body.Instructions)
						i.SequencePoint = null;

					foreach (var s in x.SequencePoints)
					{
						var instr = x.CecilMethod.Body.Instructions.SingleOrDefault(i => i.Offset == s.ILOffset);
						if (null == instr)
							continue;
						instr.SequencePoint = new Mono.Cecil.Cil.SequencePoint(sourceDocument)
						{
							StartLine = s.StartLocation.Line,
							EndLine = s.EndLocation.Line,
							StartColumn = s.StartLocation.Column,
							EndColumn = s.EndLocation.Column
						};
					}

					foreach (var l in x.LocalVariables)
					{
						if (l.OriginalVariable != null && l.OriginalVariable.Index < x.CecilMethod.Body.Variables.Count)
							x.CecilMethod.Body.Variables[l.OriginalVariable.Index].Name = l.Name;
					}

					writer.Write(x.CecilMethod.Body);
				}
			}
			using (var fsAsm = File.OpenRead(node.LoadedAssembly.FileName))
				m = ModuleDefinition.ReadModule(fsAsm);
			byte[] header;
			var debugHeader = m.GetDebugHeader(out header);
			var guidBytes = new byte[16];
			Array.Copy(header, 4, guidBytes, 0, 16);
			FixUpSignature(pdbPath, new Guid(guidBytes), BitConverter.ToInt32(header, 20));
		}

		static void FixUpSignature(string pdbFilePath, Guid newGuid, int age)
		{
			using (var read = File.Open(pdbFilePath, FileMode.Open, FileAccess.ReadWrite))
			{
				BitAccess bits = new BitAccess(512 * 1024);
				PdbFileHeader head = new PdbFileHeader(read, bits);
				PdbReader reader = new PdbReader(read, head.pageSize);
				MsfDirectory dir = new MsfDirectory(reader, head, bits);
				reader.Seek(dir.streams[1].pages[0], 8);
				reader.reader.Write(BitConverter.GetBytes(age), 0, 4);
				reader.reader.Write(newGuid.ToByteArray(), 0, 16);
				reader.Seek(dir.streams[3].pages[0], 8);
				reader.reader.Write(BitConverter.GetBytes(age), 0, 4);
			}
		}
	}
}
