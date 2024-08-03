// See https://aka.ms/new-console-template for more information
using System.Net.Sockets;
using System.Text;

Console.WriteLine("Hello, World!");

var address = "46.176.44.160";
var port = 9429;


try
{
    using (TcpClient client = new TcpClient(address, port))
    {
        while (true)
        {
            NetworkStream stream = client.GetStream();

            string message = "I hacked you!";
            byte[] data = Encoding.ASCII.GetBytes(message);

            stream.Write(data, 0, data.Length);

            data = new byte[256];
            int bytesRead = stream.Read(data, 0, data.Length);
            string response = Encoding.ASCII.GetString(data, 0, bytesRead);
            Console.WriteLine($"server response: {response}");
        }
    }
}
catch (Exception ex)
{

}