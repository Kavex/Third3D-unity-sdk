using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
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

        [MenuItem("GameObject/Third/Build Avatar", false, 100)]
        private static async void Build(MenuCommand menuCommand)
        {
            try
            {
                if (!File.Exists(thumbnailPath)) throw new Exception("Missing asset.\nPlease reinstall Third SDK in the Creator Companion.");
                var avatar = (GameObject)menuCommand.context;
                if (avatar.GetComponent<VRCAvatarDescriptor>() == null) throw new Exception("No VRC Avatar Desriptor found.");
                var mobile = ValidationEditorHelpers.IsMobilePlatform();
                var stats = new AvatarPerformanceStats(mobile);
                AvatarPerformance.CalculatePerformanceStats(avatar.name, avatar, stats, mobile);
                var rating = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);

                var bundlePath = await BuildAvatar(avatar);
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

                // Build tools like Modular Avatar seem to reset the PipelineManager.blueprintId. Instead read the blueprint id from the EditorPref that the VRC SDK writes to. 
                var blueprintId = EditorPrefs.GetString("lastBuiltAssetBundleBlueprintID");
                if (string.IsNullOrEmpty(blueprintId)) throw new Exception("Blueprint ID was not set during build");

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

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    Arguments = $"/select,\"{Path.GetFullPath(zipFilePath)}\"",
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