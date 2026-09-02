using System;
using System.Threading;

namespace RemNoteQuickClipper
{
    internal static class AudioCanary
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2) { Console.Error.WriteLine("FAIL audio and ffplay paths required"); return 2; }
            try
            {
                using (var audio = new ProcessAudio(args[1]))
                {
                    var ended = new ManualResetEvent(false); audio.PlaybackEnded += delegate { ended.Set(); };
                    audio.Play(args[0]); Thread.Sleep(250);
                    if (!audio.IsPlaying) throw new InvalidOperationException("ffplay did not enter playing state");
                    if (!ended.WaitOne(30000)) throw new InvalidOperationException("playback completion event was not raised");
                    if (audio.IsPlaying) throw new InvalidOperationException("player remained active after audio ended");
                    audio.Play(args[0]); Thread.Sleep(250);
                    if (!audio.IsPlaying) throw new InvalidOperationException("replay did not start");
                    audio.Stop();
                }
                Console.WriteLine("PASS TTS completion and replay canary"); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
        }
    }
}
