﻿extern alias __uno;
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Uno.Extensions;
using Uno.UI.SourceGenerators.XamlGenerator.XamlRedirection;
using Uno.UI.SourceGenerators.XamlGenerator.Utils;
using Uno.Roslyn;
using Windows.Foundation.Metadata;
using System.Collections.Immutable;

namespace Uno.UI.SourceGenerators.XamlGenerator
{
	internal partial class XamlFileParser
	{
		private static readonly ConcurrentDictionary<CachedFileKey, CachedFile> _cachedFiles = new();
		private static readonly TimeSpan _cacheEntryLifetime = new TimeSpan(hours: 1, minutes: 0, seconds: 0);
		private readonly string _excludeXamlNamespacesProperty;
		private readonly string _includeXamlNamespacesProperty;
		private readonly string[] _excludeXamlNamespaces;
		private readonly string[] _includeXamlNamespaces;

		/// <summary>
		/// Usages of this field should be careful, since we avoid xaml parse caching if we use it.
		/// If the usage affects the parse result, you need to disable caching for the file.
		/// </summary>
		private readonly RoslynMetadataHelper _metadataHelper;

		private int _depth;

		public XamlFileParser(string excludeXamlNamespacesProperty, string includeXamlNamespacesProperty, string[] excludeXamlNamespaces, string[] includeXamlNamespaces, RoslynMetadataHelper roslynMetadataHelper)
		{
			_excludeXamlNamespacesProperty = excludeXamlNamespacesProperty;
			_excludeXamlNamespaces = excludeXamlNamespaces;

			_includeXamlNamespacesProperty = includeXamlNamespacesProperty;
			_includeXamlNamespaces = includeXamlNamespaces;

			_metadataHelper = roslynMetadataHelper;
		}

		public XamlFileDefinition[] ParseFiles(Uno.Roslyn.MSBuildItem[] xamlSourceFiles, string projectDirectory, CancellationToken cancellationToken)
		{
			return xamlSourceFiles
				.AsParallel()
				.WithCancellation(cancellationToken)
				.Select(f => InnerParseFile(f, cancellationToken))
				.Where(f => f != null)
				.ToArray()!;

			XamlFileDefinition? InnerParseFile(MSBuildItem fileItem, CancellationToken cancellationToken)
			{
				// Generate an actual TargetPath to be used with BaseUri, so that
				// it maps to the actual path in the app package.
				var targetFilePath = fileItem.GetMetadataValue("TargetPath") is { Length: > 0 } targetPath
					? targetPath
					: fileItem.GetMetadataValue("Link") is { Length: > 0 } link
						? link
						: fileItem.GetMetadataValue("Identity").Replace(projectDirectory, "");

				return ParseFile(fileItem.File, targetFilePath.Replace("\\", "/"), cancellationToken);
			}
		}

		private static void ScavengeCache()
		{
			// DateTimeOffset.Now might be expensive.
			// Investigate if using Environment.TickCount can work and is faster.
			_cachedFiles.Remove(kvp => DateTimeOffset.Now - kvp.Value.LastTimeUsed > _cacheEntryLifetime);
		}

		private XamlFileDefinition? ParseFile(AdditionalText file, string targetFilePath, CancellationToken cancellationToken)
		{
			try
			{
#if DEBUG
				Console.WriteLine("Pre-processing XAML file: {0}", file);
#endif

				var sourceText = file.GetText(cancellationToken)!;
				if (sourceText is null)
				{
					throw new Exception($"Failed to read additional file '{file.Path}'");
				}

				var cachedFileKey = new CachedFileKey(_includeXamlNamespacesProperty, _excludeXamlNamespacesProperty, file.Path, sourceText.GetChecksum());
				if (_cachedFiles.TryGetValue(cachedFileKey, out var cached))
				{
					_cachedFiles[cachedFileKey] = cached.WithUpdatedLastTimeUsed();
					ScavengeCache();
					return cached.XamlFileDefinition;
				}

				ScavengeCache();

				// Initialize the reader using an empty context, because when the tasl
				// is run under the BeforeCompile in VS IDE, the loaded assemblies are used
				// to interpret the meaning of objects, which is not correct in Uno.UI context.
				var context = new XamlSchemaContext(Enumerable.Empty<Assembly>());

				// Force the line info, otherwise it will be enabled only when the debugger is attached.
				var settings = new XamlXmlReaderSettings() { ProvideLineInfo = true };

				XmlReader document = RewriteForXBind(sourceText);

				using (var reader = new XamlXmlReader(document, context, settings, IsIncluded))
				{
					if (reader.Read())
					{
						cancellationToken.ThrowIfCancellationRequested();

						var xamlFileDefinition = Visit(reader, file, targetFilePath, cancellationToken);
						if (!reader.DisableCaching)
						{
							_cachedFiles[cachedFileKey] = new CachedFile(DateTimeOffset.Now, xamlFileDefinition);
						}

						return xamlFileDefinition;
					}
				}

				return null;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (__uno::Uno.Xaml.XamlParseException e)
			{
				throw new XamlParsingException(e.Message, null, e.LineNumber, e.LinePosition, file.Path);
			}
			catch (XmlException e)
			{
				throw new XamlParsingException(e.Message, null, e.LineNumber, e.LinePosition, file.Path);
			}
			catch (Exception e)
			{
				throw new XamlParsingException($"Failed to parse file", e, 1, 1, file.Path);
			}
		}

		private XmlReader RewriteForXBind(SourceText sourceText)
		{
			var originalString = sourceText.ToString();

			var hasxBind = originalString.Contains("{x:Bind", StringComparison.Ordinal);

			if (!hasxBind)
			{
				// No need to modify file
				return XmlReader.Create(new StringReader(originalString));
			}

			// Apply replacements to avoid having issues with the XAML parser which does not
			// support quotes in positional markup extensions parameters.
			// Note that the UWP preprocessor does not need to apply those replacements as the
			// x:Bind expressions are being removed during the first phase and replaced by "connections".
			var adjusted = XBindExpressionParser.RewriteDocumentPaths(originalString);

			return XmlReader.Create(new StringReader(adjusted));
		}

		private __uno::Uno.Xaml.IsIncludedResult IsIncluded(string localName, string namespaceUri)
		{
			if (_includeXamlNamespaces.Contains(localName))
			{
				var result = __uno::Uno.Xaml.IsIncludedResult.ForceInclude;
				return namespaceUri.Contains("using:")
					? result
					: result.WithUpdatedNamespace(XamlConstants.PresentationXamlXmlNamespace);
			}
			else if (_excludeXamlNamespaces.Contains(localName))
			{
				return __uno::Uno.Xaml.IsIncludedResult.ForceExclude;
			}

			var valueSplit = namespaceUri.Split('?');
			if (valueSplit.Length != 2)
			{
				// Not a (valid) conditional
				return __uno::Uno.Xaml.IsIncludedResult.Default;
			}

			namespaceUri = valueSplit[0];
			var elements = valueSplit[1].Split('(', ',', ')');

			var methodName = elements[0];

			switch (methodName)
			{
				case nameof(ApiInformation.IsApiContractPresent):
				case nameof(ApiInformation.IsApiContractNotPresent):
					if (elements.Length < 4 || !ushort.TryParse(elements[2].Trim(), out var majorVersion))
					{
						throw new InvalidOperationException($"Syntax error while parsing conditional namespace expression {namespaceUri}");
					}

					var isIncluded1 = methodName == nameof(ApiInformation.IsApiContractPresent) ?
						ApiInformation.IsApiContractPresent(elements[1], majorVersion) :
						ApiInformation.IsApiContractNotPresent(elements[1], majorVersion);
					return (isIncluded1
						? __uno::Uno.Xaml.IsIncludedResult.ForceInclude
						: __uno::Uno.Xaml.IsIncludedResult.ForceExclude).WithUpdatedNamespace(namespaceUri);
				case nameof(ApiInformation.IsTypePresent):
				case nameof(ApiInformation.IsTypeNotPresent):
					if (elements.Length < 2)
					{
						throw new InvalidOperationException($"Syntax error while parsing conditional namespace expression {namespaceUri}");
					}
					var expectedType = elements[1];
					var isIncluded2 = methodName == nameof(ApiInformation.IsTypePresent) ?
						ApiInformation.IsTypePresent(elements[1], _metadataHelper) :
						ApiInformation.IsTypeNotPresent(elements[1], _metadataHelper);
					return (isIncluded2
						? __uno::Uno.Xaml.IsIncludedResult.ForceIncludeWithCacheDisabled
						: __uno::Uno.Xaml.IsIncludedResult.ForceExclude).WithUpdatedNamespace(namespaceUri);
				default:
					return __uno::Uno.Xaml.IsIncludedResult.Default.WithUpdatedNamespace(namespaceUri); // TODO: support IsPropertyPresent
			}
		}

		private XamlFileDefinition Visit(XamlXmlReader reader, AdditionalText source, string targetFilePath, CancellationToken cancellationToken)
		{
			WriteState(reader);

			var xamlFile = new XamlFileDefinition(source.Path, targetFilePath, source.GetText(cancellationToken)?.GetChecksum() ?? ImmutableArray<byte>.Empty);

			do
			{
				cancellationToken.ThrowIfCancellationRequested();
				switch (reader.NodeType)
				{
					case XamlNodeType.StartObject:
						_depth++;
						xamlFile.Objects.Add(VisitObject(reader, null));
						break;

					case XamlNodeType.NamespaceDeclaration:
						xamlFile.Namespaces.Add(reader.Namespace);
						break;

					default:
						throw new InvalidOperationException();
				}
			}
			while (reader.Read());

			return xamlFile;
		}

		private void WriteState(XamlXmlReader reader)
		{
			// Console.WriteLine(
			//	$"{new string(' ', Math.Max(0,_depth))}{reader.NodeType} {reader.Type} {reader.Member} {reader.Value}"
			// );
		}

		private XamlObjectDefinition VisitObject(XamlXmlReader reader, XamlObjectDefinition? owner)
		{
			var xamlObject = new XamlObjectDefinition(reader.Type, reader.LineNumber, reader.LinePosition, owner);

			Visit(reader, xamlObject);

			return xamlObject;
		}

		private void Visit(XamlXmlReader reader, XamlObjectDefinition xamlObject)
		{
			while (reader.Read())
			{
				WriteState(reader);

				switch (reader.NodeType)
				{
					case XamlNodeType.StartMember:
						_depth++;
						xamlObject.Members.Add(VisitMember(reader, xamlObject));
						break;

					case XamlNodeType.StartObject:
						_depth++;
						xamlObject.Objects.Add(VisitObject(reader, xamlObject));
						break;

					case XamlNodeType.Value:
						xamlObject.Value = reader.Value;
						break;

					case XamlNodeType.EndObject:
						_depth--;
						return;

					case XamlNodeType.EndMember:
						_depth--;
						break;

					default:
						throw new InvalidOperationException();
				}
			}
		}

		private XamlMemberDefinition VisitMember(XamlXmlReader reader, XamlObjectDefinition owner)
		{
			var member = new XamlMemberDefinition(reader.Member, reader.LineNumber, reader.LinePosition, owner);
			var lastWasLiteralInline = false;
			var lastWasTrimSurroundingWhiteSpace = false;
			while (reader.Read())
			{
				WriteState(reader);

				switch (reader.NodeType)
				{
					case XamlNodeType.EndMember:
						_depth--;
						return member;

					case XamlNodeType.Value:
						if (IsLiteralInlineText(reader.Value, member, owner))
						{
							var run = ConvertLiteralInlineTextToRun(reader, trimStart: !reader.PreserveWhitespace && lastWasTrimSurroundingWhiteSpace);
							member.Objects.Add(run);
							lastWasLiteralInline = true;
							lastWasTrimSurroundingWhiteSpace = false;
						}
						else
						{
							lastWasLiteralInline = false;
							lastWasTrimSurroundingWhiteSpace = false;
							if (member.Value is string s)
							{
								member.Value += ", " + reader.Value;
							}
							else
							{
								member.Value = reader.Value;
							}
						}
						break;

					case XamlNodeType.StartObject:
						_depth++;
						var obj = VisitObject(reader, owner);
						if (!reader.PreserveWhitespace &&
							lastWasLiteralInline &&
							obj.Type.TrimSurroundingWhitespace &&
							member.Objects.Count > 0 &&
							member.Objects[member.Objects.Count - 1].Members.Single() is { Value: string previousValue } runDefinition)
						{
							runDefinition.Value = previousValue.TrimEnd();
						}

						lastWasLiteralInline = false;
						lastWasTrimSurroundingWhiteSpace = obj.Type.TrimSurroundingWhitespace;
						member.Objects.Add(obj);
						break;

					case XamlNodeType.EndObject:
						lastWasLiteralInline = false;
						lastWasTrimSurroundingWhiteSpace = false;
						_depth--;
						break;

					case XamlNodeType.NamespaceDeclaration:
						lastWasLiteralInline = false;
						lastWasTrimSurroundingWhiteSpace = false;
						// Skip
						break;

					default:
						throw new InvalidOperationException("Unable to process {2} node at Line {0}, position {1}".InvariantCultureFormat(reader.LineNumber, reader.LinePosition, reader.NodeType));
				}
			}

			return member;
		}

		private static bool IsLiteralInlineText(object value, XamlMemberDefinition member, XamlObjectDefinition xamlObject)
		{
			return value is string
				&& (
					xamlObject.Type.Name is "TextBlock"
						or "Bold"
						or "Hyperlink"
						or "Italic"
						or "Underline"
						or "Span"
						or "Paragraph"
				)
				&& (member.Member.Name == "_UnknownContent" || member.Member.Name == "Inlines");
		}

		private XamlObjectDefinition ConvertLiteralInlineTextToRun(XamlXmlReader reader, bool trimStart)
		{
			var runType = new XamlType(
				XamlConstants.PresentationXamlXmlNamespace,
				"Run",
				new List<XamlType>(),
				new XamlSchemaContext()
			);

			var textMember = new XamlMember("Text", runType, false);

			return new XamlObjectDefinition(runType, reader.LineNumber, reader.LinePosition, null)
			{
				Members =
				{
					new XamlMemberDefinition(textMember, reader.LineNumber, reader.LinePosition)
					{
						Value = trimStart ? ((string)reader.Value).TrimStart() : reader.Value
					}
				}
			};
		}
	}
}
