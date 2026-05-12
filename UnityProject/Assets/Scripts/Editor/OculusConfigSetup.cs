using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Run from MetaGimbalVision > Configure Oculus Project
// Sets Quest 3 as target device and enables hand tracking support.
public static class OculusConfigSetup
{
    [MenuItem("MetaGimbalVision/Configure Oculus Project")]
    public static void Configure()
    {
        var config = OVRProjectConfig.CachedProjectConfig;
        if (config == null)
        {
            Debug.LogError("[OculusConfigSetup] OVRProjectConfig not found. " +
                           "Ensure com.meta.xr.sdk.core is installed.");
            return;
        }

        config.targetDeviceTypes = new List<OVRProjectConfig.DeviceType>
        {
            OVRProjectConfig.DeviceType.Quest3,
            OVRProjectConfig.DeviceType.Quest2,
        };

        config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;

        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();

        Debug.Log("[OculusConfigSetup] OVRProjectConfig updated — Quest 2 + Quest 3, controllers + hands.");
    }
}
