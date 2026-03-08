using System.Media;

namespace DeejNG.Services
{
    public class AudioCueService
    {
        private static readonly SoundPlayer _enableCue = new SoundPlayer("Assets/Sounds/enable.wav");
        private static readonly SoundPlayer _disableCue = new SoundPlayer("Assets/Sounds/disable.wav");

        public static void PlayEnableCue()
        {
            _enableCue.Play(); // async, non-blocking
        }

        public static void PlayDisableCue()
        {
            _disableCue.Play(); // async, non-blocking
        }
    }
}
