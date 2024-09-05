using System;
using System.Threading;
using System.Threading.Tasks;
using DigitalWorldOnline.Commons.Entities;

public class BotController
{
    private bool isBotting;
    private CancellationTokenSource cts;

    public BotController()
    {
        isBotting = false;
        cts = new CancellationTokenSource();
    }

    public bool IsBotting => isBotting; // Property to check if botting is active

    public async Task ToggleBottingAsync(GameClient client,bool start)
    {
        if (start)
        {
            if (!isBotting)
            {
                isBotting = true;
                cts = new CancellationTokenSource();
                await StartBottingAsync(client,cts.Token);
            }
        }
        else
        {
            if (isBotting)
            {
                isBotting = false;
                cts.Cancel();
                // Optionally wait for the botting task to complete cleanly
                await Task.Delay(200); // Give some time for the bot to stop
            }
        }
    }

    private async Task StartBottingAsync(GameClient client,CancellationToken token)
    {
        // Task for continuous item pickup
        var itemPickupTask = Task.Run(async () =>
        {

            while (!token.IsCancellationRequested)
            {
                //if (client.Tamer.HasAura && client.Tamer.Aura.ItemInfo.Section != 2100)
                //{
                //    IntPtr hWnd = KeySender.FindWindowByExecutableName("GDMO.exe");
                //    if (hWnd != IntPtr.Zero)
                //    {
                //        await KeySender.SendKeyToWindowAsync(hWnd,0x34); // Pick up items number 4
                //    }
            //}
                if (client.Tamer.Partner.HpRate < 120 || (client.Tamer.Partner.CurrentDs / (double)client.Tamer.Partner.DS) * 100 <  30)
                {
                    IntPtr hWnd = KeySender.FindWindowByExecutableName("GDMO.exe");
                    if (hWnd != IntPtr.Zero)
                    {
                        await KeySender.SendKeyToWindowAsync(hWnd,0x37); //HEAL number 7
                    }
                }
                await Task.Delay(500); // Adjust the delay as needed
            }
        },token);

        // Main botting loop
        while (isBotting)
        {
            if (token.IsCancellationRequested)
            {
                Console.WriteLine("Botting stopped.");
                return;
            }

            // Find the window handle for gdmo.exe
            IntPtr hWnd = KeySender.FindWindowByExecutableName("GDMO.exe");

            if (hWnd == IntPtr.Zero)
            {
                Console.WriteLine("Game window not found!");
                await Task.Delay(5000); // Wait before retrying to find the window
                continue; // Retry finding the window
            }

            // Handle non-battle state
            if (!client.Tamer.InBattle)
            {
                await Task.Delay(1500); // Adding a delay to avoid rapid firing
                await KeySender.SendKeyToWindowAsync(hWnd,0x09); // Send TAB key
                await Task.Delay(600);
                await KeySender.SendKeyToWindowAsync(hWnd,0x31); // Perform action

                // Return to the beginning of the loop to check conditions again
                continue; // Go back to the start of the main loop
            }

            // Handle battle state
            if (client.Tamer.InBattle)
            {
                await Task.Delay(100);
                await KeySender.SendKeyToWindowAsync(hWnd,0x31); // Start the battle
                await Task.Delay(500);
                await KeySender.SendKeyToWindowAsync(hWnd,0x72); // Press F3
                await Task.Delay(1000);
                await KeySender.SendKeyToWindowAsync(hWnd,0x71); // Press F2
                await Task.Delay(2000);
                await KeySender.SendKeyToWindowAsync(hWnd,0x70); // Press F1

                // Add a delay to ensure the battle actions complete
            }
        }

        //Ensure the item pickup task completes when botting stops
       await itemPickupTask;
    }

}





