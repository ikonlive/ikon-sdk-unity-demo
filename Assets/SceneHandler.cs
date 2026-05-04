using Ikon.Common.Core;
using Ikon.Common.Core.Protocol;
using Ikon.Sdk;
using LogType = Ikon.Common.Core.Protocol.LogType;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SceneHandler : MonoBehaviour
{
    public TMP_InputField ChatInputField;
    public TMP_Text ChatOutputText;
    public TMP_Text SendButtonText;
    public ButtonHandler SendButtonHandler;
    public GameObject SendButton;

    private IkonClient _ikonClient;
    private readonly ConcurrentQueue<System.Action> _mainThreadActions = new();
    private readonly Dictionary<string, AudioStream> _audioStreams = new();
    private AudioClip _recordingAudioClip;
    private bool _shouldStartRecording;
    private bool _shouldStopRecording;
    private bool _areFirstSamples;
    private int _previousMicrophonePosition;
    private int _recordingAudioChannels;

    private const int PlaybackAudioSampleRate = 48000;
    private const int RecordingAudioSampleRate = 24000;

    private class AudioStream
    {
        public GameObject AudioSourceObject;
        public AudioSourceHandler AudioSourceHandler;
    }

    public void Awake()
    {
        Application.targetFrameRate = 30;

        var audioConfig = AudioSettings.GetConfiguration();
        audioConfig.sampleRate = PlaybackAudioSampleRate;
        AudioSettings.Reset(audioConfig);
    }

    public async void Start()
    {
        Log.Instance.LogEvent += OnLogEvent;
        Log.Instance.Info($"Ikon AI C# SDK, version: {Ikon.Sdk.Version.VersionString}");

        SendButtonHandler.PressStart += OnSendButtonPressStart;
        SendButtonHandler.PressStop += OnSendButtonPressStop;

        bool useProductionEndpoint = Environment.GetEnvironmentVariable("IKON_SDK_USE_PROD_ENDPOINT")?.Trim()
            .Equals("true", StringComparison.InvariantCultureIgnoreCase) ?? true;

        var config = new IkonClientConfig
        {
            ApiKey = new ApiKeyConfig
            {
                // Get the API key from the Ikon Portal and then supply it with e.g. environment variable. Do not hardcode it.
                ApiKey = Environment.GetEnvironmentVariable("IKON_SDK_API_KEY") ??
                         throw new Exception("API key is missing. Please set the 'IKON_SDK_API_KEY' environment variable."),

                // Get the space ID from Ikon Portal. This can be hardcoded.
                SpaceId = Environment.GetEnvironmentVariable("IKON_SDK_SPACE_ID") ?? "<<SET_SPACE_ID_HERE>>",

                // Set a unique ID for the player. This can be the player's ID in your game. This can be hardcoded.
                ExternalUserId = Environment.GetEnvironmentVariable("IKON_SDK_USER_ID") ?? "<<SET_USER_ID_HERE>>",

                // Set the channel key to use
                ChannelKey = Environment.GetEnvironmentVariable("IKON_SDK_CHANNEL_KEY") ?? "<<SET_CHANNEL_KEY_HERE>>",

                BackendType = useProductionEndpoint ? BackendType.Production : BackendType.Development,
                UserType = UserType.Human,
            },

            Description = "Ikon AI SDK Unity Example",
            DeviceId = Utils.GenerateDeviceId(),
            ProductId = "Ikon.Sdk.DotNet.Examples.Unity",
            InstallId = "1",
            OpcodeGroupsFromServer = Opcode.GROUP_ALL,
            OpcodeGroupsToServer = Opcode.GROUP_ALL
        };

        _ikonClient = new IkonClient(config);
        _ikonClient.MessageReceivedAsync += OnMessageReceived;
        _ikonClient.AudioInputStreamBeginAsync += OnAudioStreamBegin;
        _ikonClient.AudioInputFrameAsync += OnAudioFrame;
        _ikonClient.AudioInputStreamEndAsync += OnAudioStreamEnd;

        await _ikonClient.ConnectAsync();
        await _ikonClient.SignalReadyAsync();
    }

    public async void OnApplicationQuit()
    {
        if (_ikonClient != null)
        {
            await _ikonClient.DisposeAsync();
            _ikonClient = null;
        }
    }

    public void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Application.Quit();
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (!string.IsNullOrWhiteSpace(ChatInputField.text))
            {
                SendCurrentInput();
            }
        }

        while (_mainThreadActions.TryDequeue(out var action))
        {
            action.Invoke();
        }

        SendButtonText.text = string.IsNullOrWhiteSpace(ChatInputField.text) ? "Record" : "Send";

        HandleAudioRecording();
    }

    private void HandleAudioRecording()
    {
        if (_shouldStartRecording)
        {
            _recordingAudioClip = Microphone.Start(null, false, 60, RecordingAudioSampleRate);
            _recordingAudioChannels = _recordingAudioClip.channels;
            _areFirstSamples = true;
            _previousMicrophonePosition = 0;
            _shouldStartRecording = false;
            SendButton.GetComponent<Image>().color = Color.green;
        }
        else if (_recordingAudioClip != null)
        {
            int currentMicrophonePosition = Microphone.GetPosition(null);
            int samplesLength = currentMicrophonePosition - _previousMicrophonePosition;

            // Sometimes the Unity microphone does not work (usually the first recording), so stop recording
            if (samplesLength < 0)
            {
                samplesLength = 0;
                _shouldStopRecording = true;
            }

            if (samplesLength > 0)
            {
                float[] samples = new float[samplesLength * _recordingAudioChannels];
                _recordingAudioClip.GetData(samples, _previousMicrophonePosition);
                _previousMicrophonePosition = currentMicrophonePosition;
                _ = _ikonClient.SendAudioAsync(samples, RecordingAudioSampleRate, _recordingAudioChannels, _areFirstSamples, _shouldStopRecording); // First samples should be sent with IsFirst=true
                _areFirstSamples = false;
            }

            // If any samples were sent, then it should be made sure that the last samples are sent with IsLast=true
            if (samplesLength == 0 && _shouldStopRecording && !_areFirstSamples)
            {
                _ = _ikonClient.SendAudioAsync(new float[_recordingAudioChannels], RecordingAudioSampleRate, _recordingAudioChannels, false, true);
            }

            if (_shouldStopRecording)
            {
                Microphone.End(null);
                _recordingAudioClip = null;
                _shouldStopRecording = false;
                SendButton.GetComponent<Image>().color = Color.white;
            }
        }
    }

    private void SendCurrentInput()
    {
#pragma warning disable CS0618
        _ikonClient.SendTextLegacy(ChatInputField.text, sendBackToSender: true);
#pragma warning restore CS0618
        ChatInputField.text = string.Empty;
        ChatInputField.ActivateInputField();
    }

    private Task OnMessageReceived(object sender, MessageEventArgs e)
    {
        switch (e.Message.Opcode)
        {
            case Opcode.ACTION_TEXT_OUTPUT:
            {
                var payload = e.Message.GetPayload<ActionTextOutput>();
                _mainThreadActions.Enqueue(() =>
                {
                    ChatOutputText.text += $"{payload.UserId}: {payload.Text}\n\n";
                });
                break;
            }

            case Opcode.ACTION_SPEECH_RECOGNIZED:
            {
                var payload = e.Message.GetPayload<ActionSpeechRecognized>();
                if (payload.WasSuccessful)
                {
                    Debug.Log($"Speech recognized: {payload.Text}");
                }
                else
                {
                    Debug.LogWarning("Speech could not be recognized");
                }
                break;
            }
        }

        return Task.CompletedTask;
    }

    private void OnSendButtonPressStart()
    {
        if (string.IsNullOrWhiteSpace(ChatInputField.text))
        {
            _shouldStartRecording = true;
            _shouldStopRecording = false;
        }
    }

    private void OnSendButtonPressStop()
    {
        if (!string.IsNullOrWhiteSpace(ChatInputField.text))
        {
            SendCurrentInput();
        }
        else
        {
            _shouldStopRecording = true;
        }
    }

    private Task OnAudioStreamBegin(object sender, AudioInputStreamBeginEventArgs e)
    {
        e.SampleRate = PlaybackAudioSampleRate;

        if (!_audioStreams.ContainsKey(e.StreamId))
        {
            _mainThreadActions.Enqueue(() =>
            {
                string audioSourceName = $"AudioSource{_audioStreams.Count + 1}";
                var audioSourceObject = new GameObject(audioSourceName);
                var audioSource = audioSourceObject.AddComponent<AudioSource>();
                var audioSourceHandler = audioSourceObject.AddComponent<AudioSourceHandler>();
                audioSourceHandler.Channels = e.ChannelCount;
                audioSource.clip = AudioClip.Create(audioSourceName, e.SampleRate * e.ChannelCount * 10, e.ChannelCount, e.SampleRate, true);
                audioSource.loop = true;
                audioSource.Play();

                _audioStreams[e.StreamId] = new AudioStream
                {
                    AudioSourceObject = audioSourceObject,
                    AudioSourceHandler = audioSourceHandler,
                };
            });
        }

        return Task.CompletedTask;
    }

    private Task OnAudioFrame(object sender, AudioInputFrameEventArgs e)
    {
        if (_audioStreams.TryGetValue(e.StreamId, out var audioStream))
        {
            audioStream.AudioSourceHandler.AddSamples(e.Samples);
        }

        return Task.CompletedTask;
    }

    private Task OnAudioStreamEnd(object sender, AudioInputStreamEndEventArgs e)
    {
        if (_audioStreams.TryGetValue(e.StreamId, out var audioStream))
        {
            _mainThreadActions.Enqueue(() =>
            {
                Destroy(audioStream.AudioSourceObject);
                _audioStreams.Remove(e.StreamId);
            });
        }

        return Task.CompletedTask;
    }

    private void OnLogEvent(object sender, LogEvent logEvent)
    {
        switch (logEvent.Type)
        {
            case LogType.Warning:
            {
                Debug.LogWarning($"{logEvent.Type}: {logEvent.Message}");
                break;
            }

            case LogType.Error:
            case LogType.Critical:
            {
                Debug.LogError($"{logEvent.Type}: {logEvent.Message}");
                break;
            }

            default:
                Debug.Log($"{logEvent.Type}: {logEvent.Message}");
                break;
        }
    }
}
