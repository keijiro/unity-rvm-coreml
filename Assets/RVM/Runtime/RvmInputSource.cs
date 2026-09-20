using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;

namespace Rvm
{

internal readonly struct SourceFrame
{
    public Texture Texture { get; }
    public double FrameInterval { get; }

    public SourceFrame(Texture texture, float frameRate)
    {
        Texture = texture;
        FrameInterval = 1.0 / frameRate;
    }
}

[RequireComponent(typeof(VideoPlayer))]
public sealed class RvmInputSource : MonoBehaviour
{
    const float CameraStartTimeout = 5;
    const int RequestedCameraWidth = 1280;
    const int RequestedCameraHeight = 720;
    const int RequestedCameraFrameRate = 30;
    const float FallbackFrameRate = 30;
    const string SelectedSourcePreferenceKey = "RVM.SelectedInputSource";

    enum InputSourceKind
    {
        Camera,
        Video
    }

    readonly struct InputSource
    {
        public InputSourceKind Kind { get; }
        public string Location { get; }
        public string DisplayName { get; }

        public InputSource(InputSourceKind kind, string location, string displayName)
        {
            Kind = kind;
            Location = location;
            DisplayName = displayName;
        }
    }

    List<InputSource> _sources;
    List<string> _sourceNames;
    VideoPlayer _videoPlayer;
    WebCamTexture _webcam;
    RenderTexture _orientedInput;
    Coroutine _switchCoroutine;
    Coroutine _loopCoroutine;
    VideoPlayer.EventHandler _videoPrepareCompleted;
    VideoPlayer.ErrorEventHandler _videoErrorReceived;
    VideoPlayer.FrameReadyEventHandler _videoFrameReadyHandler;
    VideoPlayer.EventHandler _videoLoopPointReached;
    int _selectedSourceIndex = -1;
    int _currentSourceIndex = -1;
    int _sourceGeneration;
    bool _videoFrameReady;
    string _statusMessage;

    internal event Action SynchronizationResetRequested;

    internal IReadOnlyList<string> SourceNames => _sourceNames;
    internal int SelectedSourceIndex => _selectedSourceIndex;
    internal string StatusMessage => _statusMessage;

    internal void Initialize()
    {
        _videoPlayer = GetComponent<VideoPlayer>();
        ConfigureVideoPlayer();
        if (_sources != null) return;

        _sources = EnumerateSources();
        _sourceNames = _sources.Select(source => source.DisplayName).ToList();
        var selectedSource = PlayerPrefs.GetString(SelectedSourcePreferenceKey);
        _selectedSourceIndex = _sources.FindIndex(
            source => source.DisplayName == selectedSource
        );
        if (_selectedSourceIndex < 0 && _sources.Count > 0) _selectedSourceIndex = 0;
    }

    internal void Release()
    {
        _sourceGeneration++;
        StopAllCoroutines();
        _switchCoroutine = null;
        StopCurrentSource();
        _videoPlayer = null;
    }

    internal void SelectSource(int index)
    {
        if (index < 0 || index >= _sources.Count)
        {
            _selectedSourceIndex = -1;
            SetStatus("No camera or MP4 input was found.");
            return;
        }
        if (index == _currentSourceIndex && _switchCoroutine == null) return;

        _selectedSourceIndex = index;
        PlayerPrefs.SetString(
            SelectedSourcePreferenceKey,
            _sources[index].DisplayName
        );
        PlayerPrefs.Save();
        if (_switchCoroutine != null) StopCoroutine(_switchCoroutine);
        _switchCoroutine = StartCoroutine(SwitchSource(index));
    }

    internal bool TryGetFrame(out SourceFrame frame)
    {
        frame = default;
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count)
            return false;

        Texture texture;
        if (_sources[_currentSourceIndex].Kind == InputSourceKind.Camera)
        {
            if (_webcam == null || !_webcam.isPlaying || !_webcam.didUpdateThisFrame ||
                _webcam.width <= 16 || _webcam.height <= 16)
                return false;
            texture = _webcam.videoVerticallyMirrored ?
                GetOrientedInput(_webcam) : _webcam;
        }
        else
        {
            texture = _videoPlayer.texture;
            if (!_videoFrameReady || texture == null ||
                texture.width <= 0 || texture.height <= 0)
                return false;
            _videoFrameReady = false;
        }

        frame = new SourceFrame(texture, GetFrameRate());
        return true;
    }

    static List<InputSource> EnumerateSources()
    {
        var sources = new List<InputSource>();
        var duplicateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var device in WebCamTexture.devices)
        {
            var baseName = $"Camera · {device.name}";
            duplicateCounts.TryGetValue(baseName, out var count);
            duplicateCounts[baseName] = ++count;
            var displayName = count == 1 ? baseName : $"{baseName} ({count})";
            sources.Add(new InputSource(InputSourceKind.Camera, device.name, displayName));
        }

        foreach (var relativePath in EnumerateVideoPaths(Application.streamingAssetsPath))
        {
            sources.Add(new InputSource(
                InputSourceKind.Video,
                relativePath,
                $"Video · {relativePath}"
            ));
        }
        return sources;
    }

    // Kept as a pure helper so editor validation can verify the same recursive,
    // relative-path ordering used by the runtime catalog.
    internal static string[] EnumerateVideoPaths(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(
                Path.GetExtension(path),
                ".mp4",
                StringComparison.OrdinalIgnoreCase
            ))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    void ConfigureVideoPlayer()
    {
        _videoPlayer.playOnAwake = false;
        _videoPlayer.source = VideoSource.Url;
        _videoPlayer.renderMode = VideoRenderMode.APIOnly;
        _videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
        _videoPlayer.isLooping = false;
        _videoPlayer.skipOnDrop = true;
        _videoPlayer.sendFrameReadyEvents = true;
    }

    IEnumerator SwitchSource(int index)
    {
        var generation = ++_sourceGeneration;
        StopCurrentSource();
        SetStatus($"Switching to {_sources[index].DisplayName}…");

        SynchronizationResetRequested?.Invoke();
        _currentSourceIndex = index;

        var source = _sources[index];
        if (source.Kind == InputSourceKind.Camera)
            yield return StartCamera(source, generation);
        else
            StartVideo(source, generation);

        if (generation == _sourceGeneration) _switchCoroutine = null;
    }

    IEnumerator StartCamera(InputSource source, int generation)
    {
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
        if (generation != _sourceGeneration) yield break;
        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            SetSourceError("Camera access was denied. Select a video to continue.");
            yield break;
        }

        try
        {
            _webcam = new WebCamTexture(
                source.Location,
                RequestedCameraWidth,
                RequestedCameraHeight,
                RequestedCameraFrameRate
            );
            _webcam.Play();
        }
        catch (Exception exception)
        {
            _webcam = null;
            SetSourceError($"Camera could not start: {exception.Message}");
            yield break;
        }

        var deadline = Time.realtimeSinceStartup + CameraStartTimeout;
        while (generation == _sourceGeneration && _webcam.isPlaying &&
               (_webcam.width <= 16 || _webcam.height <= 16) &&
               Time.realtimeSinceStartup < deadline)
            yield return null;
        if (generation != _sourceGeneration) yield break;
        if (!_webcam.isPlaying || _webcam.width <= 16 || _webcam.height <= 16)
        {
            _webcam.Stop();
            _webcam = null;
            SetSourceError($"Camera did not produce frames: {source.Location}.");
            yield break;
        }

        SetStatus($"Camera ready · {source.Location}");
    }

    void StartVideo(InputSource source, int generation)
    {
        var path = Path.Combine(Application.streamingAssetsPath, source.Location);
        if (!File.Exists(path))
        {
            SetSourceError($"Video file is missing: {source.Location}.");
            return;
        }

        _videoPrepareCompleted = player => OnVideoPrepared(player, generation);
        _videoErrorReceived = (player, message) =>
            OnVideoError(player, message, generation);
        _videoFrameReadyHandler = (player, frame) =>
            OnVideoFrameReady(player, generation);
        _videoLoopPointReached = player => OnVideoLoopPoint(player, generation);
        _videoPlayer.prepareCompleted += _videoPrepareCompleted;
        _videoPlayer.errorReceived += _videoErrorReceived;
        _videoPlayer.frameReady += _videoFrameReadyHandler;
        _videoPlayer.loopPointReached += _videoLoopPointReached;

        try
        {
            _videoPlayer.url = new Uri(path).AbsoluteUri;
            _videoPlayer.Prepare();
        }
        catch (Exception exception)
        {
            DetachVideoEvents();
            SetSourceError($"Video could not start: {exception.Message}");
        }
    }

    void OnVideoPrepared(VideoPlayer player, int generation)
    {
        if (!IsCurrentVideoEvent(player, generation)) return;
        _videoFrameReady = false;
        player.Play();
        SetStatus($"Video ready · {_sources[_currentSourceIndex].Location}");
    }

    void OnVideoError(VideoPlayer player, string message, int generation)
    {
        if (!IsCurrentVideoEvent(player, generation)) return;
        SetSourceError($"Video decode failed: {message}");
    }

    void OnVideoFrameReady(VideoPlayer player, int generation)
    {
        if (!IsCurrentVideoEvent(player, generation)) return;
        _videoFrameReady = true;
    }

    void OnVideoLoopPoint(VideoPlayer player, int generation)
    {
        if (!IsCurrentVideoEvent(player, generation)) return;
        if (_loopCoroutine != null) StopCoroutine(_loopCoroutine);
        _loopCoroutine = StartCoroutine(RestartVideoLoop(generation));
    }

    IEnumerator RestartVideoLoop(int generation)
    {
        _videoPlayer.Pause();
        _videoFrameReady = false;
        SynchronizationResetRequested?.Invoke();
        _videoPlayer.frame = 0;
        yield return null;
        if (generation != _sourceGeneration) yield break;
        _videoPlayer.Play();
        _loopCoroutine = null;
    }

    void StopCurrentSource()
    {
        if (_loopCoroutine != null)
        {
            StopCoroutine(_loopCoroutine);
            _loopCoroutine = null;
        }
        if (_webcam != null)
        {
            _webcam.Stop();
            Destroy(_webcam);
            _webcam = null;
        }
        ReleaseOrientedInput();

        DetachVideoEvents();
        if (_videoPlayer != null) _videoPlayer.Stop();
        _videoFrameReady = false;
        _currentSourceIndex = -1;
    }

    void DetachVideoEvents()
    {
        if (_videoPlayer == null) return;
        if (_videoPrepareCompleted != null)
            _videoPlayer.prepareCompleted -= _videoPrepareCompleted;
        if (_videoErrorReceived != null)
            _videoPlayer.errorReceived -= _videoErrorReceived;
        if (_videoFrameReadyHandler != null)
            _videoPlayer.frameReady -= _videoFrameReadyHandler;
        if (_videoLoopPointReached != null)
            _videoPlayer.loopPointReached -= _videoLoopPointReached;
        _videoPrepareCompleted = null;
        _videoErrorReceived = null;
        _videoFrameReadyHandler = null;
        _videoLoopPointReached = null;
    }

    // VideoPlayer may deliver callbacks after a source switch. Both checks are
    // required because the component is reused while its event generation changes.
    bool IsCurrentVideoEvent(VideoPlayer player, int generation) =>
        generation == _sourceGeneration && player == _videoPlayer;

    float GetFrameRate()
    {
        if (_sources[_currentSourceIndex].Kind == InputSourceKind.Camera)
            return RequestedCameraFrameRate;

        var frameRate = (float)_videoPlayer.frameRate;
        return frameRate > 0 ? frameRate : FallbackFrameRate;
    }

    Texture GetOrientedInput(Texture source)
    {
        if (_orientedInput == null || _orientedInput.width != source.width ||
            _orientedInput.height != source.height)
        {
            ReleaseOrientedInput();
            _orientedInput = new RenderTexture(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB
            )
            {
                name = "RVM Oriented Camera Input",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _orientedInput.Create();
        }

        Graphics.Blit(source, _orientedInput, new Vector2(1, -1), new Vector2(0, 1));
        return _orientedInput;
    }

    void ReleaseOrientedInput()
    {
        if (_orientedInput == null) return;
        _orientedInput.Release();
        Destroy(_orientedInput);
        _orientedInput = null;
    }

    void SetSourceError(string message)
    {
        SynchronizationResetRequested?.Invoke();
        SetStatus(message);
    }

    void SetStatus(string message) => _statusMessage = message;
}

} // namespace Rvm
