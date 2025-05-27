using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;

using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.CSharp;

public static class GodotStuff
{
	public const string BACKING_FIELD_PREFIX = "backing_";


	public static void RemoveExtraneousFiles(List<ProjectItemInfo> files, string TargetDirectory)
	{
		// get every file that contains GodotPlugins.Game
		var gameFiles = files
			.Where(f => f.FileName.Contains("GodotPlugins.Game") || f.FileName.Contains("AssemblyInfo.cs"))
			.ToList();
		// remove them from the output
		foreach (var file in gameFiles)
		{
			var path = Path.Combine(TargetDirectory, file.FileName);
			// delete them from the disk
			try
			{
				File.Delete(path);
			}
			catch (IOException)
			{
				// ignore
			}

			files.Remove(file);
		}

		RemoveDirIfEmpty(Path.Combine(TargetDirectory, "GodotPlugins.Game"));
		RemoveDirIfEmpty(Path.Combine(TargetDirectory, "Properties"));
	}

	private static void RemoveDirIfEmpty(string dir)
	{
		if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0)
		{
			try
			{
				Directory.Delete(dir);
			}
			catch (IOException)
			{
				// ignore
			}
		}
	}

	public static bool IsGodotPartialClass(ITypeDefinition entity)
	{
		// check if the entity is a member of a type that derives from GodotObject
		return entity != null && entity.GetAllBaseTypes().Any(t => t.Name == "GodotObject");
	}

	public static string FindScriptPathInChildren(IEnumerable<AstNode> children)
	{
		// using StreamWriter w = new StreamWriter(Path.Combine(TargetDirectory, file.Key));
		foreach (var child in children)
		{
			switch (child)
			{
				case TypeDeclaration typeDeclaration:
				{
					// check for the "ScriptPath" attribute"
					foreach (var attrSection in typeDeclaration.Attributes)
					{
						foreach (var attr in attrSection.Attributes)
						{
							if (attr.Type.ToString() == "ScriptPath")
							{
								// get the value of the attribute
								string scriptPath = attr.Arguments.First().ToString();

								// remove the quotes
								scriptPath = scriptPath.Substring(1, scriptPath.Length - 2);
								// remove "res://" from the beginning
								scriptPath = scriptPath.Substring(6);
								return scriptPath;
							}
						}
					}

					break;
				}
				case NamespaceDeclaration namespaceDeclaration:
				{
					var scriptPath = FindScriptPathInChildren(namespaceDeclaration.Children);
					if (scriptPath != "")
					{
						return scriptPath;
					}

					break;
				}
			}
		}

		return "";
	}

	public static string EnsureCorrectGodotPath(IGrouping<string, TypeDefinitionHandle> file, SyntaxTree syntaxTree,
		string TargetDirectory)
	{
		var path = System.IO.Path.Combine(TargetDirectory, file.Key);
		var scriptPath = GodotStuff.FindScriptPathInChildren(syntaxTree.Children);

		// chec
		if (scriptPath != "")
		{
			// ensure the directory exists for new_path
			string dir = Path.GetDirectoryName(scriptPath);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(TargetDirectory, dir)))
			{
				var fullDirPath = Path.Combine(TargetDirectory, dir);
				try
				{
					Directory.CreateDirectory(fullDirPath);
				}
				catch (IOException)
				{
					// File.Delete(dir);
					try
					{
						Directory.CreateDirectory(fullDirPath);
					}
					catch (IOException)
					{
						// ignore
					}
				}
			}

			path = Path.Combine(TargetDirectory, scriptPath);
		}

		return path;
	}

	public static bool IsSignalDelegate(IEntity entity)
	{
		var attributes = entity.GetAttributes();

		// check if any of the attributes are "Signal"
		if (attributes.Any(a => a.AttributeType.FullName == "Godot.SignalAttribute"))
		{
			return true;
		}

		if (attributes.Any(a => a.AttributeType.Name == "SignalAttribute"))
		{
			return true;
		}

		return false;
	}

	public static IEnumerable<IType> GetSignalsInClass(ITypeDefinition entity)
	{
		return entity.NestedTypes.Where(IsSignalDelegate);
	}

	public static bool IsBackingSignalDelegateField(IEntity entity)
	{
		if (entity is IField field)
		{
			return field.Name.StartsWith(BACKING_FIELD_PREFIX) &&
			       GetSignalsInClass(field.DeclaringTypeDefinition).Contains(field.Type);
		}

		return false;
	}

	public static IEnumerable<IEntity> GetBackingSignalDelegateFieldsInClass(ITypeDefinition entity)
	{
		return entity.Fields.Where(IsBackingSignalDelegateField);
	}

	public static IEnumerable<string> GetBackingSignalDelegateFieldNames(ITypeDefinition entity)
	{
		return GetBackingSignalDelegateFieldsInClass(entity).Select(f => f.Name);
	}

	public static bool IsBannedGodotTypeMember(IEntity entity)
	{
		if (!IsGodotPartialClass(entity.DeclaringTypeDefinition))
		{
			return false;
		}

		switch (entity)
		{
			case IField field:
				if (IsBackingSignalDelegateField(field))
				{
					return true;
				}

				break;
			case IProperty property:

				break;
			case IMethod method:
				// check if the method is a method that is generated by the Godot source generator
				if (GetGodotSourceGeneratorMethodNames().Contains(method.Name))
				{
					return true;
				}

				break;
			case IEvent @event:
				if (GetSignalsInClass(@event.DeclaringTypeDefinition).Contains(@event.ReturnType) &&
				    GetBackingSignalDelegateFieldNames(@event.DeclaringTypeDefinition)
					    .Contains(BACKING_FIELD_PREFIX + @event.Name))
				{
					return true;
				}

				break;
			case ITypeDefinition type:
				var bannedEmbeddedClasses = new List<string> { "MethodName", "PropertyName", "SignalName" };
				var enclosingClass = type.DeclaringTypeDefinition;
				// check if the type is a nested type
				var enclosingClassBase = enclosingClass?.DirectBaseTypes;
				// check if the type is one of the banned embedded classes, and also derives from the base class's embedded class
				if (enclosingClass != null && bannedEmbeddedClasses.Contains(type.Name) &&
				    type.DirectBaseTypes.Any(t => t.FullName.Contains(enclosingClassBase.First().Name)))
				{
					return true;
				}

				break;
			default:
				break;
		}

		var attributes = entity.GetAttributes();
		// I think we got all the banned methods and generated classes, so we don't need this anymore
		// check to see if any of the attributes are System.ComponentModel.EditorBrowsableAttribute with a System.ComponentModel.EditorBrowsableState.Never argument
		// if (attributes.Any(a => 
		// 	    a.AttributeType.FullName == "System.ComponentModel.EditorBrowsableAttribute" 
		// 	    && a.FixedArguments is [{ Value: (int)EditorBrowsableState.Never or EditorBrowsableState.Never } _]))
		// {
		// 	return true;
		// }


		return false;
	}

	// function to get a list of method names that are compiler generated, returns a collection of strings
	public static IEnumerable<string> GetGodotSourceGeneratorMethodNames()
	{
		List<string> banned_godot_methods = new List<string> {
			"GetGodotSignalList",
			"GetGodotMethodList",
			"GetGodotPropertyList",
			"GetGodotPropertyDefaultValues",
			"InvokeGodotClassStaticMethod",
			"InvokeGodotClassMethod",
			"AddEditorConstructors",
			"InternalCreateInstance",
			"HasGodotClassSignal",
			"HasGodotClassMethod",
			"GetGodotClassPropertyValue",
			"SetGodotClassPropertyValue",
			"SaveGodotObjectData",
			"RestoreGodotObjectData",
			"RaiseGodotClassSignalCallbacks"
		};
		return banned_godot_methods;

		// TODO: this, but in the meantime, just return a list of static string
		// In ScriptManagerBridge.cs, there are a number of type.GetMethod calls with the first parameter being a string
		// Get the GodotSharp module dependency from the main module
		// var godotSharpModule = module.Compilation.Modules.FirstOrDefault(m => m.AssemblyName == "GodotSharp");
		// if (godotSharpModule == null)
		// {
		// 	// We can't find it, just return true;
		// 	return Enumerable.Empty<string>();
		// }
		// // Find any top-level type definitions in the GodotSharp module that contain "SourceGenerators" in their name
		// var source_gens = godotSharpModule.TopLevelTypeDefinitions.Where(t => t.FullName.Contains("SourceGenerators"));
		//
		// var returned_collection = new List<string>();
		//
		// // Find any top-level type definitions in the GodotSharp module that contain "Bridge" in their name
		// var bridge = godotSharpModule.TopLevelTypeDefinitions.FirstOrDefault(t => t.Name == "ScriptManagerBridge");
		// if (bridge == null)
		// {
		// 	return Enumerable.Empty<string>();
		// }
		//
		// // get all the non-compiler generated methods in the bridge type
		// var methods = bridge.Methods.Where(m => !m.IsCompilerGenerated());
		//
		// foreach (IMethod method in methods)
		// {
		// 	// check to see if the method has a body
		// 	if (method.HasBody)
		// 	{
		// 		var method_def = godotSharpModule.MetadataFile.Metadata.GetMethodDefinition((MethodDefinitionHandle)method.MetadataToken);
		// 		var methodBody = godotSharpModule.MetadataFile.GetMethodBody(method_def.RelativeVirtualAddress);
		// 		// Read the IL and get the ILFunction 
		// 		ILReader ilReader = new ILReader(methodBody);
		// 		var ilFunction = 
		// 		
		// 		
		// 		
		// 		
		// 		var thing = "";
		// 		if (!thing.Contains(".getMethod"))
		// 		{
		// 			continue;
		// 		}
		// 		// check the method body for any strings (i.e. surrounded by quotes)
		// 		if (thing.Contains("\""))
		// 		{
		// 			// get all the strings using the regex '"(.*?)"'
		// 			var matches = Regex.Matches(thing, "\"(.*?)\"");
		// 			foreach (Match match in matches)
		// 			{
		// 				returned_collection.Add(match.Value);
		// 			}
		// 		}
		//
		// 	}
		// 	
		// }
		// return returned_collection;
	}
}