using System;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using Newtonsoft.Json;
using System.Xml;

namespace eo_music
{
    class Program
    {
        static byte[] FSB5_HEADER = new byte[] { 0x46, 0x53, 0x42, 0x35 };
        static string FSB_TMP_PATH = "tmp.fsb";
        static string OGG_TMP_PATH = "tmp.ogg";
        static int TARGET_SAMPLE_RATE = 48000;
        static string GetValidFilePath(string prompt)
        {
            Console.Write(prompt);
            string res = Console.ReadLine().Replace("\"", "");
            while (!File.Exists(res))
            {
                Console.WriteLine("Please enter a valid path!");
                Console.Write(prompt);
                res = Console.ReadLine().Replace("\"", "");
            }
            return res;
        }

        static List<FSBHeaderInfo> GetFSBHeadersInfo(byte[] data)
        {
            List<FSBHeaderInfo> headers = new List<FSBHeaderInfo>();
            int index = 0;
            while (index < data.Length - 3)
            {
                if (data[index] == FSB5_HEADER[0] &&
                    data[index + 1] == FSB5_HEADER[1] &&
                    data[index + 2] == FSB5_HEADER[2] &&
                    data[index + 3] == FSB5_HEADER[3])
                {
                    var headerInfo = new FSBHeaderInfo
                    {
                        Offset = index,
                        Size = BitConverter.ToUInt32(data, index + 4),
                        NumSamples = BitConverter.ToUInt32(data, index + 16),
                        SampleRate = BitConverter.ToUInt32(data, index + 44),
                        NumChannels = BitConverter.ToUInt32(data, index + 48)
                    };
                    headers.Add(headerInfo);
                    index += (int)headerInfo.Size;
                }
                else
                {
                    index++;
                }
            }
            return headers;
        }

        static bool RemuxToFSB(string input, string output, int? loop_start = null, int? loop_end = null)
        {
            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = "oggvorbis2fsb5.exe";
            p.StartInfo.Arguments = $"\"{input}\" \"{output}\" {(loop_start.HasValue ? (int)loop_start.Value : "")} {(loop_end.HasValue ? (int)loop_end.Value : "")}";
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            p.StandardOutput.ReadToEnd();
            return File.Exists(output);
        }

        struct AudioInfo
        {
            public int channels;
            public int sample_rate;
            public int samples;
            public int bits_per_second;
        }

        static bool ConvertFile(string input, string output)
        {
            AudioInfo original_audio_info = GetAudioInfo(input);
            if (original_audio_info.sample_rate == TARGET_SAMPLE_RATE && Path.GetExtension(input) == ".ogg")
            {
                File.Copy(input, output, true);
                return File.Exists(output);
            }

            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = "sox\\sox.exe";
            p.StartInfo.Arguments = $"\"{input}\" -r {TARGET_SAMPLE_RATE} \"{output}\"";

            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            p.StandardOutput.ReadToEnd();

            return true;
        }

        struct FSBHeaderInfo
        {
            public int Offset;
            public uint Size;
            public uint NumSamples;
            public uint SampleRate;
            public uint NumChannels;
        }

        static AudioInfo GetAudioInfo(string input)
        {
            var p = new System.Diagnostics.Process();
            p.StartInfo.FileName = "sox\\sox.exe";
            p.StartInfo.Arguments = $"--i \"{input}\"";
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            string data = p.StandardOutput.ReadToEnd().Replace("\r\n", "\n");

            AudioInfo audioInfo = new AudioInfo();

            Regex regex;

            regex = new Regex(@"(?<=Sample Rate    : )(.*)(?=\n)");
            audioInfo.sample_rate = int.Parse(regex.Match(data).Value);

            regex = new Regex(@"(?<== )(.*)(?= samples)");
            audioInfo.samples = int.Parse(regex.Match(data).Value);

            regex = new Regex(@"(?<=Channels       : )(.*)(?=\n)");
            audioInfo.channels = int.Parse(regex.Match(data).Value);

            regex = new Regex(@"(?<=Precision      : )(.*)(?=-bit\n)");
            audioInfo.bits_per_second = int.Parse(regex.Match(data).Value);

            return audioInfo;
        }

        static bool PatchAudioBundle(string input_bundle, string input_fsb_path, AudioInfo audioInfo, string output_bundle)
        {
            Console.WriteLine("Reading fsb data into byte array...");
            byte[] fsb_data = File.ReadAllBytes(input_fsb_path);

            var am = new AssetsManager();
            List<AssetsReplacer> assets_replacer = new List<AssetsReplacer>();

            Console.WriteLine($"Loading {input_bundle}...");
            // Original bundle file
            var og_bun = am.LoadBundleFile(input_bundle);

            Console.WriteLine("Loading bundle's assets into memory...");
            // Original Assets
            var og_assetInst = am.LoadAssetsFileFromBundle(og_bun, 0, false); // aka currentFile
            string song_name = "";
            string source_path = "";
            for (int i = 0; i < og_assetInst.table.assetFileInfo.Length; i++)
            {
                var asset = og_assetInst.table.assetFileInfo[i];
                if (asset.curFileType != (uint)AssetClassID.AudioClip)
                    continue;

                var baseField = am.GetTypeInstance(og_assetInst, asset).GetBaseField();
                song_name = baseField.Get("m_Name").GetValue().AsString();

                Console.WriteLine($"Found AudioClip {baseField.Get("m_Name").GetValue().AsString()}! Setting the proper information...");
                AssetTypeValueField resource = baseField.Get("m_Resource");

                source_path = resource.Get("m_Source").GetValue().AsString();
                resource.Get("m_Size").GetValue().Set(fsb_data.Length);
                baseField.Get("m_Length").GetValue().Set((float)audioInfo.samples / (float)audioInfo.sample_rate);
                baseField.Get("m_Frequency").GetValue().Set(audioInfo.sample_rate);
                baseField.Get("m_Channels").GetValue().Set(audioInfo.channels);
                baseField.Get("m_Channels").GetValue().Set(audioInfo.channels);


                assets_replacer.Add(new AssetsReplacerFromMemory(0, asset.index, (int)asset.curFileType, 0xFFFF, baseField.WriteToByteArray()));
                break;
            }

            Console.WriteLine("Creating new assets data...");
            byte[] newAssetData;
            using (var stream = new MemoryStream())
            using (var writer = new AssetsFileWriter(stream))
            {
                // Write the new assets in and convert it to a byte array
                og_assetInst.file.Write(writer, 0, assets_replacer, 0);
                newAssetData = stream.ToArray();
            }

            Console.WriteLine("Creating new bundle replacement data...");

            string search_path = source_path;
            if (source_path.StartsWith("archive:/"))
                search_path = search_path[9..];
            search_path = Path.GetFileName(search_path);

            List<BundleReplacer> bunReplacers = new List<BundleReplacer>()
            {
                new BundleReplacerFromMemory(og_assetInst.name, null, true, newAssetData, -1),
                new BundleReplacerFromMemory(search_path, null, false, fsb_data, -1)
            };

            using (var stream = new MemoryStream())
            using (var writer = new AssetsFileWriter(stream))
            {

                og_bun.file.Write(writer, bunReplacers);
                //Console.WriteLine($"Scanning for {source_path}...");
                //long FileDataOffset = og_bun.file.bundleHeader6.GetFileDataOffset();
                //long infoOffset = 0;

                //if (source_path != null)
                //{
                //    string search_path = source_path;
                //    if (source_path.StartsWith("archive:/"))
                //        search_path = search_path[9..];
                //    search_path = Path.GetFileName(search_path);

                //    AssetBundleDirectoryInfo06[] dirInf = og_bun.file.bundleInf6.dirInf;
                //    bool foundFile = false;
                //    //byte[] data;
                //    for (int y = 0; y < dirInf.Length; y++)
                //    {
                //        AssetBundleDirectoryInfo06 info = dirInf[y];
                //        if (info.name == search_path)
                //        {
                //            Console.WriteLine($"Found {source_path}! Storing offset and updating decompressed size...");
                //            info.decompressedSize = fsb_data.Length;
                //            infoOffset = info.offset;
                //            foundFile = true;
                //            break;
                //        }
                //    }

                //    // Write new bundle information to stream
                //    Console.WriteLine("Writing new bundle into the writer...");
                //    og_bun.file.Write(writer, bunReplacers);

                //    Console.WriteLine($"Overwriting {source_path} data with new fsb...");
                //    //writer.Position = FileDataOffset + infoOffset + (long)offset;
                //    //writer.Write(fsb_data);

                //    if (!foundFile)
                //        Console.WriteLine("resS was detected but no file was found in bundle");
                //}

                // Unload everything
                Console.WriteLine("Unloading everything...");
                am.UnloadAll();


                // Write the new bundle to the output
                Console.WriteLine($"Saving to {output_bundle}...");
                string output_path = Path.GetDirectoryName(output_bundle);
                if (!Directory.Exists(output_path) && output_path != String.Empty)
                    Directory.CreateDirectory(output_path);
                File.WriteAllBytes(output_bundle, stream.ToArray());
            }
            return File.Exists(output_bundle);
        }

        static void FixByteArray(ref byte[] data)
        {
            List<int> indexes = new List<int>();
            Console.WriteLine("Scanning for FSB5 Headers...");
            for (int i = 0; i < data.Length; i++)
            {
                int ahead = data.Length - i;
                if (ahead < 4)
                    break;
                byte[] grp = data[i..(i + 4)];
                if (grp.SequenceEqual(FSB5_HEADER))
                    indexes.Add(i);
            }

            if (indexes.Count > 1)
            {
                Console.WriteLine("Found multiple FSB5 Headers! Cutting out everything between the first and the last...");
                List<byte> res = data.ToList();
                res.RemoveRange(indexes.First(), indexes.Last() - indexes.First());
                data = res.ToArray();
            }
        }

        static void Main(string[] args)
        {
            Console.Write("Do you want to output FSB headers info as a JSON file? (y/N) ");
            bool outputJson = Console.ReadLine().ToLower() == "y";

            if (outputJson)
            {
                string jsonInputBundle = GetValidFilePath("Enter the music bundle file path (you can just drag n drop it in here): ").Replace("\"", "");
                // Output FSB headers info as JSON
                var fsbHeadersInfo = GetFSBHeadersInfo(File.ReadAllBytes(jsonInputBundle));
                string json = JsonConvert.SerializeObject(fsbHeadersInfo, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText("fsb_headers_info.json", json);
                Console.WriteLine("FSB headers info has been saved as fsb_headers_info.json.");
            }

            string input_bundle = GetValidFilePath("Enter the original music bundle file path (you can just drag n drop it in here): ").Replace("\"", "");
            string input_track = GetValidFilePath("Enter your music file (drag n drop or type it in like a chad): ").Replace("\"", "");

            Console.Write("Do you want to loop the track? (y/N) ");
            bool loop_setting = Console.ReadLine().ToLower() == "y";

            int loop_start = 0;
            int loop_end = 0;

            if (loop_setting)
            {
                {
                    {
                        Console.Write("Enter loop start sample: ");
                        loop_start = int.Parse(Console.ReadLine());
                        Console.Write("Enter loop end sample (put in -1 if you want it to set the loop point to be the end of the song): ");
                        loop_end = int.Parse(Console.ReadLine());

                        AudioInfo original_audio_info = GetAudioInfo(input_track);

                        if (loop_end == -1)
                            loop_end = original_audio_info.samples - original_audio_info.sample_rate; // Set the end loop to be total time - 1 second

                        if (original_audio_info.sample_rate != TARGET_SAMPLE_RATE)
                        {
                            Console.WriteLine($"Updating loop samples (from {original_audio_info.sample_rate} to {TARGET_SAMPLE_RATE})...");
                            float diffHz = (float)TARGET_SAMPLE_RATE / (float)(original_audio_info.sample_rate);
                            loop_start = (int)((float)loop_start * diffHz);
                            loop_end = (int)((float)loop_end * diffHz);
                        }
                    }

                    Console.Write("Enter save path of new bundle (must include <filename>.bundle in the end): ");
                    string output_bundle = Console.ReadLine().Replace("\"", "");

                    // Output FSB headers info as JSON
                    var fsbHeadersInfo = GetFSBHeadersInfo(File.ReadAllBytes(input_bundle));
                    string json = JsonConvert.SerializeObject(fsbHeadersInfo, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText("fsb_headers_info.json", json);

                    if (File.Exists(OGG_TMP_PATH))
                        File.Delete(OGG_TMP_PATH);
                    if (File.Exists(FSB_TMP_PATH))
                        File.Delete(FSB_TMP_PATH);

                    Console.WriteLine();

                    Console.WriteLine($"Converting {input_track} to ogg for remuxing...");
                    ConvertFile(input_track, OGG_TMP_PATH);

                    Console.WriteLine("Remuxing ogg into a fsb for injecting into bundle...");
                    if (loop_setting)
                        RemuxToFSB(OGG_TMP_PATH, FSB_TMP_PATH, loop_start, loop_end);
                    else
                        RemuxToFSB(OGG_TMP_PATH, FSB_TMP_PATH);

                    Console.WriteLine("Getting ogg audio info for asset modification...");
                    AudioInfo audio_info = GetAudioInfo(OGG_TMP_PATH);

                    if (PatchAudioBundle(input_bundle, FSB_TMP_PATH, audio_info, output_bundle))
                        Console.WriteLine("Success! Your new custom bundle with your own track has been made!");
                    else
                        Console.WriteLine("The function didn't return a true, so something probably went wrong!");

                    if (File.Exists(OGG_TMP_PATH))
                        File.Delete(OGG_TMP_PATH);
                    if (File.Exists(FSB_TMP_PATH))
                        File.Delete(FSB_TMP_PATH);

                    Console.ReadLine();
                }
            }
        }
    }
}