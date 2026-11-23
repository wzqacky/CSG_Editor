using UnityEngine;
using UnityEngine.Rendering;

// Distinguishes the six probe requests from the camera that displays the splats.
[DisallowMultipleComponent]
public sealed class TexelSplatCaptureCamera : MonoBehaviour
/*
A small marker and message carrier attached to the probe's capture camera
- Identify the camera
- Pass info between classes
*/
{
    [System.NonSerialized]
    public RTHandle DepthTarget;

    [System.NonSerialized]
    public bool DepthPassRecorded;
}
