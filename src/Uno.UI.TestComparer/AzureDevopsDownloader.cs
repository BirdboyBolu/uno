﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using Refit;

namespace Uno.UI.TestComparer
{
	class AzureDevopsDownloader
	{
		private readonly string _pat;
		private readonly string _collectionUri;

		public AzureDevopsDownloader(string pat, string collectionUri)
		{
			_pat = pat;
			_collectionUri = collectionUri;
		}

		public async Task DownloadArtifacts(string basePath, string project, string definitionName, string artifactName, string sourceBranch, string branchName, int buildId, int runLimit)
		{
			Directory.CreateDirectory(basePath);

			var connection = new VssConnection(new Uri(_collectionUri), new VssBasicCredential(string.Empty, _pat));

			var client = await connection.GetClientAsync<BuildHttpClient>();

			Console.WriteLine($"Getting definitions ({basePath}, {project}, {definitionName}, {artifactName}, {sourceBranch}, {branchName}, {buildId}, {runLimit})");
			var definitions = await client.GetDefinitionsAsync(project, name: definitionName);

			Console.WriteLine("Getting builds");
			var builds = await client.GetBuildsAsync(project, definitions: new[] { definitions.First().Id }, branchName: branchName, top: runLimit, queryOrder: BuildQueryOrder.FinishTimeDescending, statusFilter: BuildStatus.Completed);

			var currentBuild = await client.GetBuildAsync(project, buildId);

			foreach (var build in builds.Concat(new[] { currentBuild }))
			{
				var tempFile = Path.GetTempFileName();

				Console.WriteLine($"Getting artifact for build {build.Id}");
				using (var stream = await client.GetArtifactContentZipAsync(project, build.Id, artifactName))
				{
					using (var f = File.OpenWrite(tempFile))
					{
						await stream.CopyToAsync(f);
					}
				}

				var fullPath = Path.Combine(basePath, $"{build.LastChangedDate:yyyyMMdd-hhmmss}-{build.Id}");

				Console.WriteLine($"Extracting artifact for build {build.Id}");
				ZipFile.ExtractToDirectory(tempFile, fullPath);

				foreach (var file in Directory.EnumerateFiles(fullPath, "*.*", SearchOption.AllDirectories))
				{
					if (Path.GetDirectoryName(file) != fullPath)
					{
						var destFileName = Path.Combine(fullPath, Path.GetFileName(file));

						if (!File.Exists(destFileName))
						{
							File.Move(file, destFileName);
						}
					}
				}
			}
		}
	}
}
