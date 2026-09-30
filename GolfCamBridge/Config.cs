using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GolfCamBridge
{
    [DataContract]
    public sealed class BridgeConfig
    {
        /// <summary>Folder containing softcam_golfcam1.dll / softcam_golfcam2.dll. Relative paths are resolved from the exe folder.</summary>
        [DataMember(Order = 1)] public string SoftcamDir { get; set; }

        /// <summary>Debayer algorithm (SpinnakerNET ColorProcessingAlgorithm): HQ_LINEAR (quality) or NEAREST_NEIGHBOR (fastest).</summary>
        [DataMember(Order = 2)] public string ColorAlgorithm { get; set; }

        /// <summary>"OnDemand": open the cameras only while an app (Premier) uses a Golf Cam. "AlwaysOn": keep streaming.</summary>
        [DataMember(Order = 3)] public string Mode { get; set; }

        /// <summary>OnDemand: release the cameras this many seconds after the last app disconnected.</summary>
        [DataMember(Order = 4)] public int IdleReleaseSeconds { get; set; }

        /// <summary>Frame rate of the "Standby" placeholder image while the real camera is off.</summary>
        [DataMember(Order = 5)] public int StandbyFps { get; set; }

        [DataMember(Order = 6)] public CameraConfig[] Cameras { get; set; }

        public bool AlwaysOn => string.Equals(Mode, "AlwaysOn", System.StringComparison.OrdinalIgnoreCase);

        public BridgeConfig() { SetDefaults(default); }

        [OnDeserializing]
        private void SetDefaults(StreamingContext _)
        {
            SoftcamDir = @"..\bin";
            ColorAlgorithm = "HQ_LINEAR";
            Mode = "OnDemand";
            IdleReleaseSeconds = 60;
            StandbyFps = 10;
            Cameras = new CameraConfig[0];
        }

        public static BridgeConfig Load(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(BridgeConfig));
            using (var fs = File.OpenRead(path))
                return (BridgeConfig)ser.ReadObject(fs);
        }

        public void Save(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(BridgeConfig));
            using (var fs = File.Create(path))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true, "  "))
            {
                ser.WriteObject(w, this);
                w.Flush();
            }
        }

        /// <summary>Canonical JSON (also used to compare two configs).</summary>
        public string ToJson()
        {
            var ser = new DataContractJsonSerializer(typeof(BridgeConfig));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, this);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public BridgeConfig Clone()
        {
            var ser = new DataContractJsonSerializer(typeof(BridgeConfig));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(ToJson())))
                return (BridgeConfig)ser.ReadObject(ms);
        }
    }

    [DataContract]
    public sealed class CameraConfig
    {
        /// <summary>Label shown in the tray/log, e.g. "FO" or "DTL".</summary>
        [DataMember(Order = 1)] public string Name { get; set; }

        /// <summary>Camera serial number (tray menu: "Show connected cameras"). Empty = use Index.</summary>
        [DataMember(Order = 2)] public string Serial { get; set; }
        [DataMember(Order = 3)] public int Index { get; set; }

        /// <summary>1 = "Golf Cam 1", 2 = "Golf Cam 2".</summary>
        [DataMember(Order = 4)] public int VirtualCam { get; set; }

        [DataMember(Order = 5)] public double FrameRate { get; set; }
        [DataMember(Order = 6)] public double ExposureUs { get; set; }
        [DataMember(Order = 7)] public double GainDb { get; set; }

        /// <summary>Preferred sensor output. Falls back to any Bayer*8, then Mono8.</summary>
        [DataMember(Order = 8)] public string PixelFormat { get; set; }

        /// <summary>Flip at the sensor. Leave false unless the camera is physically mounted upside down.</summary>
        [DataMember(Order = 9)] public bool ReverseX { get; set; }
        [DataMember(Order = 10)] public bool ReverseY { get; set; }

        /// <summary>Output size (centered ROI). Also the size of the virtual webcam, so keep it set (multiples of 4).</summary>
        [DataMember(Order = 11)] public int Width { get; set; }
        [DataMember(Order = 12)] public int Height { get; set; }

        public CameraConfig() { SetDefaults(default); }

        [OnDeserializing]
        private void SetDefaults(StreamingContext _)
        {
            Name = "Cam";
            Serial = "";
            Index = -1;
            VirtualCam = 1;
            FrameRate = 223;
            ExposureUs = 2000;
            GainDb = 24;
            PixelFormat = "BayerRG8";
            ReverseX = false;
            ReverseY = false;
            Width = 1440;   // BFS-U3-16S2C full sensor (IMX273)
            Height = 1080;
        }
    }
}
