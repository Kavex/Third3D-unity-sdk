# Third Unity SDK

Build Third Avatars Bundles (.3b) to use with [Third Uploader](https://github.com/third3d/uploader) and [third3d.com](https://third3d.com/).

## Install

1. Open VRChat Creator Companion
2. **Settings** -> **Packages** -> **Add Repository**
3. Insert `https://github.com/third3d/unity-sdk/releases/latest/download/index.json` into **Repository Listing URL** and click on **Add**
4. Navigate to **Projects** and click on **Manage Projects**
5. Find **Third SDK** and click on **+**

## Build Third Avatar Bundle

1. Right click the avatar in the hierarchy window of a Unity scene
2. Select **Third -> Build Avatar**
3. After the build is finished, the output directory gets opened

The _Third Avatar Bundle_ (.3b file) is saved to `{Unity project path}/ThirdBuild/{Target platform}/{Game object name}.3b`

A Third Avatar Bundle are zipped VRChat asset bundles with metadata.
