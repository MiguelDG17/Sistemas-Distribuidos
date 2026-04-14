using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Sensor
{
    class Program
    {
        static string sensorId = "S101";
        static string zona = "ZONA_CENTRO";
        static string tiposDados = "TEMP,HUM,RUIDO";
        static StreamReader? reader;
        static StreamWriter? writer;
        static bool connected = false;
        static Timer? heartbeatTimer;

        static void Main(string[] args)
        {
            string gatewayIp = "127.0.0.1";
            int gatewayPort = 5000;

            if (args.Length >= 1) gatewayIp = args[0];
            if (args.Length >= 2) int.TryParse(args[1], out gatewayPort);
            if (args.Length >= 3) sensorId = args[2];
            if (args.Length >= 4) zona = args[3];
            if (args.Length >= 5) tiposDados = args[4];

            Console.WriteLine("=== SENSOR - Monitorizacao Urbana ===");
            Console.WriteLine($"Sensor ID: {sensorId}");
            Console.WriteLine($"Zona: {zona}");
            Console.WriteLine($"Tipos de dados: {tiposDados}");
            Console.WriteLine($"Gateway: {gatewayIp}:{gatewayPort}");
            Console.WriteLine();

            // Conectar ao gateway
            TcpClient client;
            try
            {
                client = new TcpClient(gatewayIp, gatewayPort);
                NetworkStream ns = client.GetStream();
                reader = new StreamReader(ns, Encoding.UTF8);
                writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao conectar ao Gateway: {ex.Message}");
                return;
            }

            // Enviar HELLO
            Send($"HELLO|{sensorId}|{zona}|{tiposDados}");
            string? resp = reader.ReadLine();
            Console.WriteLine($"Resposta: {resp}");

            if (resp == null || !resp.StartsWith("OK"))
            {
                Console.WriteLine("Falha no registo. A terminar.");
                client.Close();
                return;
            }

            connected = true;

            // Iniciar heartbeat (a cada 30 segundos)
            heartbeatTimer = new Timer(SendHeartbeat, null, 30000, 30000);

            // Menu interativo
            ShowMenu();
            string? input;
            while (connected && (input = Console.ReadLine()) != null)
            {
                switch (input.Trim())
                {
                    case "1":
                        SendData();
                        break;
                    case "2":
                        SendHeartbeatManual();
                        break;
                    case "3":
                        Disconnect(client);
                        return;
                    default:
                        Console.WriteLine("Opcao invalida.");
                        break;
                }
                if (connected) ShowMenu();
            }

            client.Close();
        }

        static void ShowMenu()
        {
            Console.WriteLine();
            Console.WriteLine("--- Menu ---");
            Console.WriteLine("1. Enviar medicao");
            Console.WriteLine("2. Enviar heartbeat");
            Console.WriteLine("3. Desconectar");
            Console.Write("Opcao: ");
        }

        static void SendData()
        {
            Console.WriteLine($"Tipos disponiveis: {tiposDados}");
            Console.Write("Tipo de dado: ");
            string? tipo = Console.ReadLine()?.Trim().ToUpper();
            if (string.IsNullOrEmpty(tipo)) return;

            Console.Write("Valor: ");
            string? valor = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(valor)) return;

            string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            string msg = $"DATA|{sensorId}|{tipo}|{valor}|{timestamp}";
            Send(msg);

            string? resp = reader?.ReadLine();
            Console.WriteLine($"Resposta: {resp}");
        }

        static void SendHeartbeat(object? state)
        {
            if (!connected || writer == null || reader == null) return;
            try
            {
                Send($"HEARTBEAT|{sensorId}");
                string? resp = reader.ReadLine();
                Console.WriteLine($"\n[Heartbeat enviado] Resposta: {resp}");
            }
            catch
            {
                Console.WriteLine("\n[Heartbeat falhou - conexao perdida]");
                connected = false;
            }
        }

        static void SendHeartbeatManual()
        {
            Send($"HEARTBEAT|{sensorId}");
            string? resp = reader?.ReadLine();
            Console.WriteLine($"Resposta: {resp}");
        }

        static void Disconnect(TcpClient client)
        {
            heartbeatTimer?.Dispose();
            Send($"BYE|{sensorId}");
            string? resp = reader?.ReadLine();
            Console.WriteLine($"Resposta: {resp}");
            connected = false;
            client.Close();
            Console.WriteLine("Desconectado.");
        }

        static void Send(string msg)
        {
            writer?.WriteLine(msg);
            Console.WriteLine($"[ENVIADO] {msg}");
        }
    }
}
