#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace SensorSdk.ExampleUnity
{
    // iOS build: link CoreBluetooth and add the Bluetooth usage description.
    public static class SensorDemoPostBuild
    {
        [PostProcessBuild]
        public static void OnPostProcessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            string projPath = PBXProject.GetPBXProjectPath(path);
            PBXProject proj = new PBXProject();
            proj.ReadFromFile(projPath);
            string frameworkTarget = proj.GetUnityFrameworkTargetGuid();
            proj.AddFrameworkToProject(frameworkTarget, "CoreBluetooth.framework", false);
            proj.WriteToFile(projPath);

            string plistPath = Path.Combine(path, "Info.plist");
            PlistDocument plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetString("NSBluetoothAlwaysUsageDescription",
                "Bluetooth is used to connect sensor devices.");
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
