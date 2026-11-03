using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WasmChasm.Core;

namespace WasmChasm.App.Services;

/// <summary>
/// Optional sounds (off by default), mixed so they can overlap: a gentle hum while the window is active that
/// swells while purging, a gradual power-up at launch, a blip on row change, a rising ratchet on select, a retro crumble as a row breaks up, a soft chime on completion.
/// Audio is cosmetic: if there is no working device, the app simply stays silent.
/// </summary>
public sealed class SoundBank : IDisposable
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SoundSynth.Rate, 1);
    private const float IdleHumLevel = 0.6f;
    private const long MinTickGapMs = 45;

    private readonly float[] _click = SoundSynth.Click();
    private readonly float[] _tick = SoundSynth.Tick();
    private readonly float[][] _ratchet = Enumerable.Range(0, SoundSynth.RatchetTopCount + 1).Select(SoundSynth.Ratchet).ToArray();
    private readonly float[] _chime = SoundSynth.Chime();
    private readonly float[] _disintegrate = SoundSynth.Disintegrate();
    private readonly float[] _powerOn = SoundSynth.PowerOn();
    private readonly LoopVoice _hum = new(SoundSynth.AmbientHum(), fadeSeconds: 2.5);
    private readonly MixingSampleProvider _mixer = new(Format) { ReadFully = true };
    private WaveOutEvent? _output;
    private bool _enabled;
    private bool _windowActive = true;
    private bool _purging;
    private long _lastTick;

    public SoundBank()
    {
        _mixer.AddMixerInput(_hum);
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            UpdateHum();
            Safe(() =>
            {
                if (value)
                {
                    if (_output is null)
                    {
                        _output = new WaveOutEvent { DesiredLatency = 120 };
                        _output.Init(_mixer);
                    }

                    _output.Play();
                }
                else
                {
                    _output?.Stop();
                }
            });
        }
    }

    /// <summary>The hum fades out while another window is active, so it never plays over the sim.</summary>
    public bool WindowActive
    {
        get => _windowActive;
        set
        {
            _windowActive = value;
            UpdateHum();
        }
    }

    public void PlayClick() => Play(_click);
    public void PlayChime() => Play(_chime);
    public void PlayDisintegrate() => Play(_disintegrate);
    public void PlayPowerOn() => Play(_powerOn);

    /// <summary>One ratchet tooth for this many marked aircraft: each one raises the pitch a step.</summary>
    public void PlayRatchet(int markedCount) => Play(_ratchet[Math.Clamp(markedCount, 0, SoundSynth.RatchetTopCount)]);

    /// <summary>Soft blip for moving between rows. Holding an arrow key repeats fast, so it is rate-limited.</summary>
    public void PlayTick()
    {
        long now = Environment.TickCount64;
        if (now - _lastTick < MinTickGapMs) return;
        _lastTick = now;
        Play(_tick);
    }

    /// <summary>The hum swells while purging, even if the window is in the background.</summary>
    public void StartHum()
    {
        _purging = true;
        UpdateHum();
    }

    public void StopHum()
    {
        _purging = false;
        UpdateHum();
    }

    private void UpdateHum() => _hum.Level = !_enabled ? 0 : _purging ? 1 : _windowActive ? IdleHumLevel : 0;

    private void Play(float[] samples)
    {
        if (_enabled) Safe(() => _mixer.AddMixerInput(new OneShotVoice(samples)));
    }

    private static void Safe(Action a)
    {
        try { a(); } catch { /* audio is cosmetic */ }
    }

    public void Dispose()
    {
        Safe(() =>
        {
            _output?.Stop();
            _output?.Dispose();
        });
    }

    /// <summary>Plays once, then ends, which removes it from the mixer.</summary>
    private sealed class OneShotVoice : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public OneShotVoice(float[] samples) => _samples = samples;
        public WaveFormat WaveFormat => Format;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    /// <summary>Loops forever, fading in and out on request so it never pops. The mixer keeps it for the app's life.</summary>
    private sealed class LoopVoice : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly float _step;
        private int _position;
        private float _gain;
        private volatile float _target;

        public LoopVoice(float[] samples, double fadeSeconds)
        {
            _samples = samples;
            _step = (float)(1 / (fadeSeconds * SoundSynth.Rate));
        }

        public float Level { set => _target = value; }
        public WaveFormat WaveFormat => Format;

        public int Read(float[] buffer, int offset, int count)
        {
            float target = _target;
            for (int i = 0; i < count; i++)
            {
                _gain = _gain < target ? Math.Min(target, _gain + _step) : Math.Max(target, _gain - _step);
                buffer[offset + i] = _samples[_position] * _gain;
                _position = (_position + 1) % _samples.Length;
            }

            return count;
        }
    }
}
