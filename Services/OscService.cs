using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VRCNext
{
    public record OscParamDef(string Name, string Type, bool HasInput, bool HasOutput);

    public class OscService : IDisposable
    {
        private const string OSC_IP = "127.0.0.1";
        private const int VRC_LISTEN_PORT = 9001;  // VRChat sends params here
        private const int VRC_SEND_PORT = 9000;    // VRChat listens here

        private UdpClient? _receiver;
        private UdpClient? _sender;
        private CancellationTokenSource? _cts;
        private bool _running;

        public bool IsConnected => _running;

        private readonly Action<string> _log;
        private Action<string, object, string>? _onParam;   // name, value, type
        private Action<string, List<OscParamDef>>? _onAvatarChange; // avatarId, params

        public OscService(Action<string> log) { _log = log; }

        public void SetParamCallback(Action<string, object, string> cb) => _onParam = cb;
        public void SetAvatarChangeCallback(Action<string, List<OscParamDef>> cb) => _onAvatarChange = cb;

        public bool Start()
        {
            if (_running) return true;
            try
            {
                _receiver = new UdpClient(VRC_LISTEN_PORT);
                _sender = new UdpClient();
                _sender.Connect(IPAddress.Parse(OSC_IP), VRC_SEND_PORT);
                _cts = new CancellationTokenSource();
                _running = true;
                _ = ReceiveLoopAsync(_cts.Token);
                _log($"[OSC] Listening on :{VRC_LISTEN_PORT}, sending to :{VRC_SEND_PORT}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[OSC] Start failed: {ex.Message}");
                _receiver?.Close(); _receiver = null;
                _sender?.Close(); _sender = null;
                _running = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
            _receiver?.Close(); _receiver = null;
            _sender?.Close(); _sender = null;
            _log("[OSC] Stopped");
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _receiver != null)
            {
                try
                {
                    var result = await _receiver.ReceiveAsync(ct);
                    ParseOscMessage(result.Buffer);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { _log($"[OSC] Receive error: {ex.Message}"); }
            }
        }

        private void ParseOscMessage(byte[] data)
        {
            try
            {
                int pos = 0;
                string address = ReadOscString(data, ref pos);
                string typeTag = ReadOscString(data, ref pos);
                if (typeTag.Length < 2 || typeTag[0] != ',') return;

                // Avatar change
                if (address == "/avatar/change")
                {
                    if (typeTag[1] == 's')
                    {
                        string avatarId = ReadOscString(data, ref pos);
                        if (!string.IsNullOrEmpty(avatarId))
                        {
                            var paramDefs = LoadAvatarConfig(avatarId);
                            _onAvatarChange?.Invoke(avatarId, paramDefs);
                            _log($"[OSC] Avatar changed: {avatarId} ({paramDefs.Count} params in config)");
                        }
                    }
                    return;
                }

                // Avatar parameters
                if (!address.StartsWith("/avatar/parameters/")) return;

                string paramName = address.Substring("/avatar/parameters/".Length);
                if (string.IsNullOrEmpty(paramName)) return;

                char type = typeTag[1];
                object value;
                string typeName;

                switch (type)
                {
                    case 'T':
                        value = true; typeName = "bool";
                        break;
                    case 'F':
                        value = false; typeName = "bool";
                        break;
                    case 'f':
                        if (pos + 4 > data.Length) return;
                        value = ReadBigEndianFloat(data, ref pos);
                        typeName = "float";
                        break;
                    case 'i':
                        if (pos + 4 > data.Length) return;
                        value = ReadBigEndianInt(data, ref pos);
                        typeName = "int";
                        break;
                    default:
                        return;
                }

                _onParam?.Invoke(paramName, value, typeName);
            }
            catch { }
        }

        // VRChat OSC config file handling

        private static string GetOscConfigDir()
        {
            // VRChat stores data in AppData\LocalLow\VRChat\VRChat, NOT AppData\Roaming
            // Environment.SpecialFolder.ApplicationData = AppData\Roaming (wrong)
            // We have to build the LocalLow path manually.
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "AppData", "LocalLow", "VRChat", "VRChat", "OSC");
        }

        private string? FindAvatarConfigPath(string avatarId)
        {
            var oscDir = GetOscConfigDir();
            if (!Directory.Exists(oscDir)) return null;
            foreach (var userDir in Directory.GetDirectories(oscDir))
            {
                var avatarsDir = Path.Combine(userDir, "Avatars");
                var configFile = Path.Combine(avatarsDir, avatarId + ".json");
                if (File.Exists(configFile)) return configFile;
            }
            return null;
        }

        /// <summary>
        /// Finds and loads the most recently written avatar config file.
        /// Used on connect so the parameter list populates immediately without needing /avatar/change.
        /// </summary>
        public (string AvatarId, List<OscParamDef> Params) LoadMostRecentAvatarConfig()
        {
            try
            {
                var oscDir = GetOscConfigDir();
                if (!Directory.Exists(oscDir)) return ("", new());

                string? latestFile = null;
                DateTime latestTime = DateTime.MinValue;

                foreach (var userDir in Directory.GetDirectories(oscDir))
                {
                    var avatarsDir = Path.Combine(userDir, "Avatars");
                    if (!Directory.Exists(avatarsDir)) continue;
                    foreach (var file in Directory.GetFiles(avatarsDir, "avtr_*.json"))
                    {
                        var t = File.GetLastWriteTime(file);
                        if (t > latestTime) { latestTime = t; latestFile = file; }
                    }
                }

                if (latestFile == null)
                {
                    _log("[OSC] No avatar config files found — load your avatar in VRChat with OSC enabled first");
                    return ("", new());
                }

                var avatarId = Path.GetFileNameWithoutExtension(latestFile);
                var paramDefs = ParseAvatarConfigFile(latestFile);
                _log($"[OSC] Loaded most recent config: {Path.GetFileName(latestFile)} ({paramDefs.Count} params)");
                return (avatarId, paramDefs);
            }
            catch (Exception ex)
            {
                _log($"[OSC] LoadMostRecentAvatarConfig error: {ex.Message}");
                return ("", new());
            }
        }

        public List<OscParamDef> LoadAvatarConfig(string avatarId)
        {
            try
            {
                var oscDir = GetOscConfigDir();
                _log($"[OSC] Looking for config in: {oscDir}");
                var path = FindAvatarConfigPath(avatarId);
                if (path == null)
                {
                    _log($"[OSC] No config file found for {avatarId} — load avatar in VRChat with OSC enabled to generate one");
                    return new();
                }
                _log($"[OSC] Loading config: {Path.GetFileName(path)}");
                return ParseAvatarConfigFile(path);
            }
            catch (Exception ex) { _log($"[OSC] Config read error: {ex.Message}"); return new(); }
        }

        private static List<OscParamDef> ParseAvatarConfigFile(string path)
        {
            var result = new List<OscParamDef>();
            var json = File.ReadAllText(path);
            var root = JsonNode.Parse(json);

            // VRChat uses nested {input:{address,type}, output:{address,type}} format
            var parameters = root?["parameters"]?.AsArray();
            if (parameters == null) return result;

            foreach (var p in parameters)
            {
                if (p == null) continue;
                var name = p["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;

                // Nested format: { input: {address, type}, output: {address, type} }
                bool hasInput  = p["input"]  != null;
                bool hasOutput = p["output"] != null;

                string? rawType = p["input"]?["type"]?.GetValue<string>()
                               ?? p["output"]?["type"]?.GetValue<string>()
                               ?? p["type"]?.GetValue<string>(); // flat format fallback

                string type = (rawType ?? "").ToLowerInvariant() switch
                {
                    "bool"  => "bool",
                    "float" => "float",
                    "int"   => "int",
                    _       => "float"
                };

                // If neither input nor output but there's a flat "address" field, treat as both
                if (!hasInput && !hasOutput && p["address"] != null)
                {
                    hasInput = true;
                    hasOutput = true;
                }

                result.Add(new OscParamDef(name, type, hasInput, hasOutput));
            }

            return result;
        }

        /// <summary>
        /// Modifies all VRChat OSC avatar config files to enable output for every parameter.
        /// Returns the number of files updated.
        /// </summary>
        public int EnableAllOutputs()
        {
            int updated = 0;
            try
            {
                var oscDir = GetOscConfigDir();
                if (!Directory.Exists(oscDir)) return 0;
                foreach (var userDir in Directory.GetDirectories(oscDir))
                {
                    var avatarsDir = Path.Combine(userDir, "Avatars");
                    if (!Directory.Exists(avatarsDir)) continue;
                    foreach (var file in Directory.GetFiles(avatarsDir, "*.json"))
                    {
                        try
                        {
                            if (EnableOutputsInFile(file)) updated++;
                        }
                        catch (Exception ex) { _log($"[OSC] Could not update {Path.GetFileName(file)}: {ex.Message}"); }
                    }
                }
                _log($"[OSC] Enabled outputs in {updated} avatar config file(s)");
            }
            catch (Exception ex) { _log($"[OSC] EnableAllOutputs error: {ex.Message}"); }
            return updated;
        }

        private static bool EnableOutputsInFile(string path)
        {
            var json = File.ReadAllText(path);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null) return false;

            var parameters = root["parameters"]?.AsArray();
            if (parameters == null) return false;

            bool changed = false;
            foreach (var p in parameters)
            {
                if (p == null) continue;
                if (p["output"] != null) continue; // already has output

                var name = p["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;

                // Determine type from input section
                string? rawType = p["input"]?["type"]?.GetValue<string>();
                if (rawType == null) continue;

                string address = $"/avatar/parameters/{name}";
                var outputNode = new JsonObject
                {
                    ["address"] = address,
                    ["type"] = rawType
                };
                p.AsObject()["output"] = outputNode;
                changed = true;
            }

            if (changed)
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, root.ToJsonString(opts));
            }
            return changed;
        }

        // OSC binary helpers

        private static string ReadOscString(byte[] data, ref int pos)
        {
            int start = pos;
            while (pos < data.Length && data[pos] != 0) pos++;
            string s = Encoding.UTF8.GetString(data, start, pos - start);
            pos++; // skip null terminator
            int pad = 4 - (pos % 4);
            if (pad < 4) pos += pad;
            return s;
        }

        private static float ReadBigEndianFloat(byte[] data, ref int pos)
        {
            byte[] b = new byte[4];
            b[0] = data[pos + 3]; b[1] = data[pos + 2];
            b[2] = data[pos + 1]; b[3] = data[pos];
            pos += 4;
            return BitConverter.ToSingle(b, 0);
        }

        private static int ReadBigEndianInt(byte[] data, ref int pos)
        {
            int v = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            return v;
        }

        public void SendBool(string name, bool value)
        {
            if (_sender == null) { _log("[OSC] SendBool: no sender"); return; }
            try
            {
                var buf = new List<byte>();
                WriteOscString(buf, $"/avatar/parameters/{name}");
                WriteOscString(buf, value ? ",T" : ",F");
                var p = buf.ToArray();
                int sent = _sender.Send(p, p.Length);
                _log($"[OSC] → {name} = {value} ({sent}B to :{VRC_SEND_PORT})");
            }
            catch (Exception ex) { _log($"[OSC] Send bool error: {ex.Message}"); }
        }

        public void SendFloat(string name, float value)
        {
            if (_sender == null) { _log("[OSC] SendFloat: no sender"); return; }
            try
            {
                var buf = new List<byte>();
                WriteOscString(buf, $"/avatar/parameters/{name}");
                WriteOscString(buf, ",f");
                byte[] fb = BitConverter.GetBytes(value);
                buf.Add(fb[3]); buf.Add(fb[2]); buf.Add(fb[1]); buf.Add(fb[0]);
                var p = buf.ToArray();
                int sent = _sender.Send(p, p.Length);
                _log($"[OSC] → {name} = {value:F3} ({sent}B to :{VRC_SEND_PORT})");
            }
            catch (Exception ex) { _log($"[OSC] Send float error: {ex.Message}"); }
        }

        public void SendInt(string name, int value)
        {
            if (_sender == null) { _log("[OSC] SendInt: no sender"); return; }
            try
            {
                var buf = new List<byte>();
                WriteOscString(buf, $"/avatar/parameters/{name}");
                WriteOscString(buf, ",i");
                buf.Add((byte)(value >> 24));
                buf.Add((byte)(value >> 16));
                buf.Add((byte)(value >> 8));
                buf.Add((byte)(value));
                var p = buf.ToArray();
                int sent = _sender.Send(p, p.Length);
                _log($"[OSC] → {name} = {value} ({sent}B to :{VRC_SEND_PORT})");
            }
            catch (Exception ex) { _log($"[OSC] Send int error: {ex.Message}"); }
        }

        private static void WriteOscString(List<byte> buf, string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            buf.AddRange(b);
            int pad = 4 - (b.Length % 4);
            if (pad == 0) pad = 4;
            for (int i = 0; i < pad; i++) buf.Add(0);
        }

        // OSCQuery (VRChat v2023.3.1+)

        /// <summary>
        /// Tries to fetch all current avatar parameter values via VRChat's OSCQuery HTTP endpoint.
        /// VRChat serves OSCQuery on localhost:9002 by default.
        /// Returns true if at least one parameter was retrieved.
        /// </summary>
        public async Task<bool> TryOscQueryAsync(Action<string, object, string> onResult)
        {
            const int port = 9002;
            try
            {
                using var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromMilliseconds(600);
                var json = await http.GetStringAsync($"http://localhost:{port}/avatar/parameters");
                var root = JsonNode.Parse(json);
                var contents = root?["CONTENTS"]?.AsObject();
                if (contents == null) return false;

                int count = 0;
                foreach (var kv in contents)
                {
                    var name = kv.Key;
                    var node = kv.Value;
                    if (node == null) continue;

                    var typeStr = node["TYPE"]?.GetValue<string>() ?? "";
                    var valueArr = node["VALUE"]?.AsArray();

                    switch (typeStr)
                    {
                        case "T":
                            onResult(name, (object)true, "bool"); count++; break;
                        case "F":
                            onResult(name, (object)false, "bool"); count++; break;
                        case "b":
                            onResult(name, (object)(valueArr?[0]?.GetValue<bool>() ?? false), "bool"); count++; break;
                        case "f":
                            onResult(name, (object)(valueArr?[0]?.GetValue<float>() ?? 0f), "float"); count++; break;
                        case "i":
                            onResult(name, (object)(valueArr?[0]?.GetValue<int>() ?? 0), "int"); count++; break;
                    }
                }

                _log($"[OSC] OSCQuery: {count} live params from localhost:{port}");
                return count > 0;
            }
            catch (Exception ex)
            {
                _log($"[OSC] OSCQuery unavailable (port {port}): {ex.Message}");
                return false;
            }
        }

        public void Dispose() { Stop(); _cts?.Dispose(); }
    }
}
