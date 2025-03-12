using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Validation;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using Debug = UnityEngine.Debug;

namespace Third
{
    public class AvatarBuild
    {
        const string thumbnailPath = "Packages/com.third3d.sdk/Editor/.thumbnail.png";

        [MenuItem("GameObject/Third/Build Avatar", true)]
        private static bool ValidateBuild(MenuCommand menuCommand)
        {
            return Selection.activeGameObject != null &&
                Selection.activeGameObject.GetComponent<VRCAvatarDescriptor>() != null;
        }

        [MenuItem("GameObject/Third/Build All Avatars in Scene")]
        private static async void BuildAllInCurrentScenes(MenuCommand menuCommand)
        {
            var toBuild = new List<(Scene, GameObject)>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var obj in scene.GetRootGameObjects())
                {
                    if (obj.GetComponent<VRCAvatarDescriptor>() == null) continue;
                    toBuild.Add((scene, obj));
                }
            }

            var duplicates = toBuild.GroupBy(p => p.Item2.name).Where(group => group.Count() > 1).SelectMany(group => group).ToList();
            if (duplicates.Count() > 0)
            {
                var dupStr = new StringBuilder();
                foreach (var dup in duplicates) dupStr.AppendLine($"{dup.Item1.name}/{dup.Item2.name}");
                var errMsg = "Found duplicated avatars:\n\n" + dupStr + "\nAborting build.";
                EditorUtility.DisplayDialog("Third: Avatar Build Failed", errMsg, "Ok");
                Debug.LogError("Third: Avatar Build Failed\n" + errMsg);
                return;
            }

            foreach (var (scene, avatar) in toBuild)
            {
                try
                {
                    await StartBuildAvatar(avatar);
                }
                catch (Exception e)
                {
                    EditorUtility.DisplayDialog("Third: Avatar Build Failed", $"Building {avatar.name} in {scene.name} failed\n"
                        + e.Message
                        + "\nCheck the VRChat SDK or console log for further information.", "Ok");
                    throw;
                }
            }

            if (toBuild.Count() > 0)
            {
                string platform;
                switch (EditorUserBuildSettings.selectedBuildTargetGroup)
                {
                    case BuildTargetGroup.Standalone:
                        platform = "windows";
                        break;
                    case BuildTargetGroup.Android:
                        platform = "android";
                        break;
                    case BuildTargetGroup.iOS:
                        platform = "ios";
                        break;
                    default:
                        throw new Exception("Invalid build target: " + EditorUserBuildSettings.selectedBuildTargetGroup.ToString());
                }
                string destinationFolder = Path.Combine("ThirdBuild", platform);
                Process.Start("explorer.exe", $"\"{Path.GetFullPath(destinationFolder)}\"");
            }
            else
            {
                EditorUtility.DisplayDialog("Third: No Avatars Built", "No avatars found in the scene.", "Ok");
            }
        }

        [MenuItem("GameObject/Third/Build Avatar", false, 100)]
        private static async void Build(MenuCommand menuCommand)
        {
            try
            {
                var avatar = (GameObject)menuCommand.context;
                var outputPath = await StartBuildAvatar(avatar);
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    Arguments = $"/select,\"{Path.GetFullPath(outputPath)}\"",
                    FileName = "explorer.exe"
                };
                Process.Start(startInfo);
                EditorUtility.ClearProgressBar();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Third: Avatar Build Failed", e.Message + "\nCheck the VRChat SDK or console log for further information.", "Ok");
                throw;
            }
        }

        private static async Task<string> StartBuildAvatar(GameObject avatar)
        {
            if (!File.Exists(thumbnailPath)) throw new Exception("Missing asset.\nPlease reinstall Third SDK in the Creator Companion.");
            if (avatar.GetComponent<VRCAvatarDescriptor>() == null) throw new Exception("No VRC Avatar Desriptor found.");
            var mobile = ValidationEditorHelpers.IsMobilePlatform();
            var stats = new AvatarPerformanceStats(mobile);
            AvatarPerformance.CalculatePerformanceStats(avatar.name, avatar, stats, mobile);
            var rating = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);

            var pm = avatar.GetComponent<PipelineManager>();
            if (!pm)
            {
                pm = avatar.AddComponent<PipelineManager>(); // should never happen, but just in case
            }
            var validbpId = pm.blueprintId != null && Regex.IsMatch(pm.blueprintId, @"^avtr_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
            if (!validbpId)
            {
                Debug.Log("Blueprint ID invalid. Generating new Blueprint ID");
                pm.AssignId();
            }
            var bpIdBeforeBuild = pm.blueprintId;

            var bundlePath = await BuildAvatar(avatar);

            // Sometimes, something is deleting the blueprint ID? Make sure it is the same as before build.
            var bpIdAfterBuild = pm.blueprintId;
            if (bpIdAfterBuild != bpIdBeforeBuild) throw new Exception("Blueprint ID changed during build. Try building again, while logged in to the VRChat SDK.");

            // Blueprint ID has been checked before and after build.
            // Should be safe to use now.
            var blueprintId = pm.blueprintId;

            if (ValidationEditorHelpers.CheckIfAssetBundleFileTooLarge(ContentType.Avatar, bundlePath, out int fileSize, mobile))
            {
                var limit = ValidationHelpers.GetAssetBundleSizeLimit(ContentType.Avatar, mobile);
                throw new Exception(
                    $"Avatar download size is too large for the target platform. {ValidationHelpers.FormatFileSize(fileSize)} > {ValidationHelpers.FormatFileSize(limit)}");
            }

            if (ValidationEditorHelpers.CheckIfUncompressedAssetBundleFileTooLarge(ContentType.Avatar, out int fileSizeUncompressed, mobile))
            {
                var limit = ValidationHelpers.GetAssetBundleSizeLimit(ContentType.Avatar, mobile, false);
                throw new Exception(
                    $"Avatar uncompressed size is too large for the target platform. {ValidationHelpers.FormatFileSize(fileSizeUncompressed)} > {ValidationHelpers.FormatFileSize(limit)}");
            }



            EditorUtility.DisplayProgressBar("Third Avatar Archive", "Creating Archive...", 0.1f);

            string platform;
            switch (EditorUserBuildSettings.selectedBuildTargetGroup)
            {
                case BuildTargetGroup.Standalone:
                    platform = "windows";
                    break;
                case BuildTargetGroup.Android:
                    platform = "android";
                    break;
                case BuildTargetGroup.iOS:
                    platform = "ios";
                    break;
                default:
                    throw new Exception("Invalid build target: " + EditorUserBuildSettings.selectedBuildTargetGroup.ToString());
            }

            string destinationFolder = Path.Combine("ThirdBuild", platform);
            Directory.CreateDirectory(destinationFolder);

            string zipFileName = avatar.name + ".3b";
            string zipFilePath = Path.Combine(destinationFolder, zipFileName);

            // Create zip file
            if (File.Exists(zipFilePath))
            {
                File.Delete(zipFilePath);
            }
            using (ZipArchive archive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
            {

                string jsonContent = JObject.FromObject(new
                {
                    avatar.name,
                    blueprintId,
                    assetBundles = new JObject
                    {
                        [platform] = JObject.FromObject(new
                        {
                            performance = Enum.GetName(typeof(PerformanceRating), rating).ToLower(),
                            Application.unityVersion
                        })
                    }
                }).ToString();

                ZipArchiveEntry metadataEntry = archive.CreateEntry("metadata.json", CompressionLevel.Optimal);
                using (StreamWriter writer = new StreamWriter(metadataEntry.Open()))
                {
                    writer.Write(jsonContent);
                }
                EditorUtility.DisplayProgressBar("Third Avatar Archive", "Written Metadata. Writing Bundle...", 0.25f);
                ZipArchiveEntry thumbnailEntry = archive.CreateEntryFromFile(thumbnailPath, "thumbnail.png", CompressionLevel.NoCompression);
                EditorUtility.DisplayProgressBar("Third Avatar Archive", "Written Metadata. Writing Bundle...", 0.50f);
                var path = $"{platform}.vrca";
                ZipArchiveEntry bundleEntry = archive.CreateEntryFromFile(bundlePath, path, CompressionLevel.NoCompression);
                EditorUtility.DisplayProgressBar("Third Avatar Archive", "Written Bundle...", 0.99f);
            }
            File.Delete(bundlePath);
            EditorUtility.ClearProgressBar();
            return zipFilePath;
        }

        private static async Task<string> BuildAvatar(GameObject avatar)
        {
            using (var buiderApi = new AvatarBuilderApi())
            {
                return await buiderApi.GetBuilder().Build(avatar);
            }
        }

        private class AvatarBuilderApi : IDisposable
        {
            private bool _loggedIn;

            public AvatarBuilderApi()
            {
                _loggedIn = APIUser.IsLoggedIn;
                if (_loggedIn) return;

                var prop = typeof(APIUser).GetProperty("CurrentUser");
                var user = new APIUser(developerType: APIUser.DeveloperType.Internal);
                prop.SetValue(null, user);
            }

            public IVRCSdkAvatarBuilderApi GetBuilder()
            {
                var builder = new VRCSdkControlPanelAvatarBuilder();
                builder.RegisterBuilder(ScriptableObject.CreateInstance<VRCSdkControlPanel>());
                return builder;
            }

            public void Dispose()
            {
                if (_loggedIn) return;

                var prop = typeof(APIUser).GetProperty("CurrentUser");
                prop.SetValue(null, null);
            }
        }
    }
}