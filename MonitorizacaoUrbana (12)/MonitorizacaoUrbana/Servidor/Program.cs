using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Servidor
{
    class Program
    {
        static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "dados");
        // Mutex por ficheiro para acesso concorrente seguro
        static readonly Dictionary<string, object> FileLocks = new();
        static readonly object FileLocksDictLock = new object();

        static object GetFileLock(string tipoDado)
        {
            lock (FileLocksDictLock)
            {
                if (!FileLocks.ContainsKey(tipoDado))
                    FileLocks[tipoDado] = new object();
                return FileLocks[tipoDado];
            }
        }

        static void Main(string[] args)
        {
            int port = 6000;
            if (args.Length > 0) int.TryParse(args[0], out port);

            Directory.CreateDirectory(DataDir);

            TcpListener listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Console.WriteLine($"[SERVIDOR] A escutar na porta {port}...");

            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                Console.WriteLine("[SERVIDOR] Gateway conectado.");
                // Atendimento concorrente com threads
                Thread t = new Thread(() => HandleGateway(client));
                t.Start();
            }
        }

        static void HandleGateway(TcpClient client)
        {
            try
            {
                using NetworkStream ns = client.GetStream();
                using StreamReader reader = new StreamReader(ns, Encoding.UTF8);
                using StreamWriter writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    Console.WriteLine($"[SERVIDOR] Recebido: {line}");
                    string response = ProcessMessage(line);
                    writer.WriteLine(response);
                    Console.WriteLine($"[SERVIDOR] Enviado: {response}");

                    if (line.StartsWith("BYE"))
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SERVIDOR] Erro: {ex.Message}");
            }
            finally
            {
                client.Close();
                Console.WriteLine("[SERVIDOR] Gateway desconectado.");
            }
        }

        static string ProcessMessage(string message)
        {
            string[] parts = message.Split('|');
            if (parts.Length == 0) return "ERROR|Mensagem vazia";

            switch (parts[0])
            {
                case "HELLO":
                    if (parts.Length < 2) return "ERROR|Falta gateway_id";
                    Console.WriteLine($"[SERVIDOR] Gateway registado: {parts[1]}");
                    return "OK|HELLO";

                case "DATA":
                    // DATA|gateway_id|sensor_id|zona|tipo_dado|valor|timestamp
                    if (parts.Length < 7) return "ERROR|Formato DATA invalido";
                    string gatewayId = parts[1];
                    string sensorId = parts[2];
                    string zona = parts[3];
                    string tipoDado = parts[4];
                    string valor = parts[5];
                    string timestamp = parts[6];
                    SaveData(tipoDado, sensorId, zona, valor, timestamp, gatewayId);
                    return "OK|DATA";

                case "BYE":
                    if (parts.Length < 2) return "ERROR|Falta gateway_id";
                    Console.WriteLine($"[SERVIDOR] Gateway {parts[1]} a desconectar.");
                    return "OK|BYE";

                default:
                    return "ERROR|Comando desconhecido";
            }
        }

        static void SaveData(string tipoDado, string sensorId, string zona, string valor, string timestamp, string gatewayId)
        {
            string filePath = Path.Combine(DataDir, $"{tipoDado}.csv");
            string csvLine = $"{timestamp},{sensorId},{zona},{valor},{gatewayId}";

            lock (GetFileLock(tipoDado))
            {
                if (!File.Exists(filePath))
                {
                    File.WriteAllText(filePath, "timestamp,sensor_id,zona,valor,gateway_id\n");
                }
                File.AppendAllText(filePath, csvLine + "\n");
            }

            Console.WriteLine($"[SERVIDOR] Dados guardados: {tipoDado} -> {csvLine}");
        }
    }
}
