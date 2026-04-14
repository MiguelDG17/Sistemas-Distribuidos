using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Gateway
{
    class SensorInfo
    {
        public string SensorId { get; set; } = "";
        public string Estado { get; set; } = "";
        public string Zona { get; set; } = "";
        public List<string> TiposDados { get; set; } = new();
        public string LastSync { get; set; } = "";
    }

    class Program
    {
        static readonly string GatewayId = "GW01";
        static readonly string ConfigFile = Path.Combine(AppContext.BaseDirectory, "sensores.csv");
        static readonly object ConfigLock = new object();
        static Dictionary<string, SensorInfo> sensoresRegistados = new();

        // Ligação ao servidor
        static TcpClient? serverClient;
        static StreamReader? serverReader;
        static StreamWriter? serverWriter;
        static readonly object ServerLock = new object();

        static void Main(string[] args)
        {
            int sensorPort = 5000;
            string serverIp = "127.0.0.1";
            int serverPort = 6000;

            if (args.Length >= 1) int.TryParse(args[0], out sensorPort);
            if (args.Length >= 2) serverIp = args[1];
            if (args.Length >= 3) int.TryParse(args[2], out serverPort);

            // Carregar configuração de sensores
            LoadConfig();

            // Conectar ao servidor
            if (!ConnectToServer(serverIp, serverPort))
            {
                Console.WriteLine("[GATEWAY] Nao foi possivel conectar ao servidor. A terminar.");
                return;
            }

            // Escutar sensores
            TcpListener listener = new TcpListener(IPAddress.Any, sensorPort);
            listener.Start();
            Console.WriteLine($"[GATEWAY] {GatewayId} a escutar sensores na porta {sensorPort}...");

            while (true)
            {
                TcpClient sensorClient = listener.AcceptTcpClient();
                Console.WriteLine("[GATEWAY] Sensor conectado.");
                Thread t = new Thread(() => HandleSensor(sensorClient));
                t.Start();
            }
        }

        static bool ConnectToServer(string ip, int port)
        {
            try
            {
                serverClient = new TcpClient(ip, port);
                NetworkStream ns = serverClient.GetStream();
                serverReader = new StreamReader(ns, Encoding.UTF8);
                serverWriter = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };

                // Enviar HELLO ao servidor
                serverWriter.WriteLine($"HELLO|{GatewayId}");
                string? resp = serverReader.ReadLine();
                Console.WriteLine($"[GATEWAY] Resposta do servidor: {resp}");
                return resp != null && resp.StartsWith("OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GATEWAY] Erro ao conectar ao servidor: {ex.Message}");
                return false;
            }
        }

        static void HandleSensor(TcpClient client)
        {
            try
            {
                using NetworkStream ns = client.GetStream();
                using StreamReader reader = new StreamReader(ns, Encoding.UTF8);
                using StreamWriter writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    Console.WriteLine($"[GATEWAY] Recebido do sensor: {line}");
                    string response = ProcessSensorMessage(line);
                    writer.WriteLine(response);
                    Console.WriteLine($"[GATEWAY] Enviado ao sensor: {response}");

                    if (line.StartsWith("BYE"))
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GATEWAY] Erro com sensor: {ex.Message}");
            }
            finally
            {
                client.Close();
                Console.WriteLine("[GATEWAY] Sensor desconectado.");
            }
        }

        static string ProcessSensorMessage(string message)
        {
            string[] parts = message.Split('|');
            if (parts.Length == 0) return "ERROR|Mensagem vazia";

            switch (parts[0])
            {
                case "HELLO":
                    // HELLO|sensor_id|zona|tipo1,tipo2,...
                    if (parts.Length < 4) return "ERROR|Formato HELLO invalido";
                    string sensorId = parts[1];
                    string zona = parts[2];
                    string tipos = parts[3];

                    // Verificar se sensor está registado
                    lock (ConfigLock)
                    {
                        if (sensoresRegistados.ContainsKey(sensorId))
                        {
                            var s = sensoresRegistados[sensorId];
                            if (s.Estado == "desativado")
                                return "ERROR|Sensor desativado";
                            if (s.Estado == "manutencao")
                                return "ERROR|Sensor em manutencao";
                            s.LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                        }
                        else
                        {
                            // Registar novo sensor
                            var novo = new SensorInfo
                            {
                                SensorId = sensorId,
                                Estado = "ativo",
                                Zona = zona,
                                TiposDados = new List<string>(tipos.Split(',')),
                                LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
                            };
                            sensoresRegistados[sensorId] = novo;
                        }
                        SaveConfig();
                    }
                    Console.WriteLine($"[GATEWAY] Sensor {sensorId} registado na zona {zona}.");
                    return "OK|HELLO";

                case "DATA":
                    // DATA|sensor_id|tipo_dado|valor|timestamp
                    if (parts.Length < 5) return "ERROR|Formato DATA invalido";
                    string sid = parts[1];
                    string tipoDado = parts[2];
                    string valor = parts[3];
                    string timestamp = parts[4];

                    // Validar sensor
                    lock (ConfigLock)
                    {
                        if (!sensoresRegistados.ContainsKey(sid))
                            return "ERROR|Sensor nao registado";
                        var sensor = sensoresRegistados[sid];
                        if (sensor.Estado != "ativo")
                            return $"ERROR|Sensor {sensor.Estado}";
                        if (!sensor.TiposDados.Contains(tipoDado))
                            return "ERROR|Tipo de dado nao suportado";
                        sensor.LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                        SaveConfig();
                    }

                    // Encaminhar para o servidor
                    string zonaDoSensor;
                    lock (ConfigLock) { zonaDoSensor = sensoresRegistados[sid].Zona; }
                    string forwardResult = ForwardToServer(sid, zonaDoSensor, tipoDado, valor, timestamp);
                    return forwardResult;

                case "HEARTBEAT":
                    if (parts.Length < 2) return "ERROR|Falta sensor_id";
                    string hbId = parts[1];
                    lock (ConfigLock)
                    {
                        if (sensoresRegistados.ContainsKey(hbId))
                        {
                            sensoresRegistados[hbId].LastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                            SaveConfig();
                        }
                    }
                    return "OK|HEARTBEAT";

                case "BYE":
                    if (parts.Length < 2) return "ERROR|Falta sensor_id";
                    Console.WriteLine($"[GATEWAY] Sensor {parts[1]} a desconectar.");
                    return "OK|BYE";

                default:
                    return "ERROR|Comando desconhecido";
            }
        }

        static string ForwardToServer(string sensorId, string zona, string tipoDado, string valor, string timestamp)
        {
            lock (ServerLock)
            {
                try
                {
                    if (serverWriter == null || serverReader == null)
                        return "ERROR|Sem ligacao ao servidor";

                    string msg = $"DATA|{GatewayId}|{sensorId}|{zona}|{tipoDado}|{valor}|{timestamp}";
                    serverWriter.WriteLine(msg);
                    Console.WriteLine($"[GATEWAY] Enviado ao servidor: {msg}");

                    string? resp = serverReader.ReadLine();
                    Console.WriteLine($"[GATEWAY] Resposta do servidor: {resp}");
                    return resp ?? "ERROR|Sem resposta do servidor";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GATEWAY] Erro ao encaminhar: {ex.Message}");
                    return "ERROR|Falha ao encaminhar para servidor";
                }
            }
        }

        static void LoadConfig()
        {
            if (!File.Exists(ConfigFile))
            {
                // Criar ficheiro de exemplo
                File.WriteAllText(ConfigFile,
                    "S101:ativo:ZONA_CENTRO:[TEMP,HUM,RUIDO]:2026-03-10T08:45:00\n" +
                    "S102:ativo:ZONA_ESCOLAR:[PM2.5,TEMP]:2026-03-10T09:00:00\n" +
                    "S103:manutencao:ZONA_INDUSTRIAL:[AR,PM10]:2026-03-09T18:30:00\n");
                Console.WriteLine("[GATEWAY] Ficheiro de configuracao criado com dados de exemplo.");
            }

            sensoresRegistados.Clear();
            foreach (string line in File.ReadAllLines(ConfigFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // Formato: sensor_id:estado:zona:[tipos]:last_sync
                string[] parts = line.Split(':');
                if (parts.Length < 5) continue;

                string sensorId = parts[0];
                string estado = parts[1];
                string zona = parts[2];
                // Extrair tipos de dados: [TEMP,HUM,RUIDO]
                string tiposRaw = parts[3].Trim('[', ']');
                List<string> tipos = new List<string>(tiposRaw.Split(','));
                string lastSync = parts[4];
                // last_sync pode ter ':' por causa do formato da hora
                if (parts.Length > 5)
                    lastSync = string.Join(":", parts, 4, parts.Length - 4);

                sensoresRegistados[sensorId] = new SensorInfo
                {
                    SensorId = sensorId,
                    Estado = estado,
                    Zona = zona,
                    TiposDados = tipos,
                    LastSync = lastSync
                };
            }

            Console.WriteLine($"[GATEWAY] {sensoresRegistados.Count} sensor(es) carregados da configuracao.");
        }

        static void SaveConfig()
        {
            var sb = new StringBuilder();
            foreach (var kvp in sensoresRegistados)
            {
                var s = kvp.Value;
                string tipos = "[" + string.Join(",", s.TiposDados) + "]";
                sb.AppendLine($"{s.SensorId}:{s.Estado}:{s.Zona}:{tipos}:{s.LastSync}");
            }
            File.WriteAllText(ConfigFile, sb.ToString());
        }
    }
}
