// A stand-in for a RUNNING ShadeNet Downloader, for the installer's tests.
//
//   no argument   lives (at most a minute) until the "quit" event of the test is set; says nothing
//   --quit        what the real program does with that switch: asks the running copy to end, then exits. Here it only does so
//                 when the test says this stand-in obeys (SHADENET_STANDIN_OBEY=1), so both cases can be tried: a program that
//                 quits when asked and one that does not.
//
// The event name comes from SHADENET_STANDIN_EVENT (one per test). Nothing here touches the real program or its data.

var name = Environment.GetEnvironmentVariable("SHADENET_STANDIN_EVENT") ?? "ShadeNetStandIn";
using var quit = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + name);

if (args.Contains("--quit"))
{
    if (Environment.GetEnvironmentVariable("SHADENET_STANDIN_OBEY") == "1") quit.Set();
    return 0;
}

quit.WaitOne(TimeSpan.FromSeconds(60));
return 0;
