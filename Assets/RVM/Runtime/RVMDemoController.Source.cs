using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace RVM
{

public sealed partial class RVMDemoController
{
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

    const float CameraStartTimeout = 5;
    const string SelectedSourcePreferenceKey = "RVM.SelectedInputSource";

    List<InputSource> _sources;
    VideoPlayer _videoPlayer;
    WebCamTexture _webcam;
    Coroutine _switchCoroutine;
    Coroutine _loopCoroutine;
    DropdownField _sourceDropdown;
    VideoPlayer.EventHandler _videoPrepareCompleted;
    VideoPlayer.ErrorEventHandler _videoErrorReceived;
    VideoPlayer.FrameReadyEventHandler _videoFrameReadyHandler;
    VideoPlayer.EventHandler _videoLoopPointReached;
    int _selectedSourceIndex = -1;
    int _currentSourceIndex = -1;
    int _sourceGeneration;
    bool _videoFrameReady;
    string _sourceError;

    void InitializeSources()
    {
        _videoPlayer = GetComponent<VideoPlayer>();
        ConfigureVideoPlayer();
        if (_sources != null) return;

        _sources = EnumerateSources();
        var selectedSource = PlayerPrefs.GetString(SelectedSourcePreferenceKey);
        _selectedSourceIndex = _sources.FindIndex(
            source => source.DisplayName == selectedSource
        );
        if (_selectedSourceIndex < 0 && _sources.Count > 0) _selectedSourceIndex = 0;
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
    static string[] EnumerateVideoPaths(string root)
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

    void BindSourceDropdown()
    {
        if (_sourceDropdown == null) return;

        _sourceDropdown.UnregisterValueChangedCallback(OnSourceDropdownChanged);
        _sourceDropdown.choices = _sources.Select(source => source.DisplayName).ToList();
        _sourceDropdown.SetEnabled(_sources.Count > 0);
        if (_selectedSourceIndex >= 0 && _selectedSourceIndex < _sources.Count)
            _sourceDropdown.SetValueWithoutNotify(_sources[_selectedSourceIndex].DisplayName);
        else
            _sourceDropdown.SetValueWithoutNotify(string.Empty);
        _sourceDropdown.RegisterValueChangedCallback(OnSourceDropdownChanged);
    }

    void OnSourceDropdownChanged(ChangeEvent<string> change)
    {
        PlayerPrefs.SetString(SelectedSourcePreferenceKey, change.newValue);
        PlayerPrefs.Save();
        SelectSource(_sourceDropdown.index);
    }

    void SelectSource(int index)
    {
        if (index < 0 || index >= _sources.Count)
        {
            _selectedSourceIndex = -1;
            SetStatus("No camera or MP4 input was found.");
            return;
        }
        if (index == _currentSourceIndex && _switchCoroutine == null) return;

        _selectedSourceIndex = index;
        if (_sourceDropdown != null && _sourceDropdown.index != index)
            _sourceDropdown.SetValueWithoutNotify(_sources[index].DisplayName);
        if (_switchCoroutine != null) StopCoroutine(_switchCoroutine);
        _switchCoroutine = StartCoroutine(SwitchSource(index));
    }

    IEnumerator SwitchSource(int index)
    {
        var generation = ++_sourceGeneration;
        StopCurrentSource();
        SetStatus($"Switching to {_sources[index].DisplayName}…");

        // The readback callback owns its source pixels until completion. Waiting here
        // prevents an old frame from being submitted after the recurrent reset.
        while (_readbackPending) yield return null;
        if (generation != _sourceGeneration) yield break;

        yield return ResetInferenceState(generation);
        if (generation != _sourceGeneration) yield break;
        ClearDisplayTextures();
        _currentSourceIndex = index;
        _sourceError = null;

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
            _webcam = new WebCamTexture(source.Location, 1280, 720, 30);
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

        UpdateInputImage();
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
        _videoErrorReceived = (player, message) => OnVideoError(player, message, generation);
        _videoFrameReadyHandler = (player, frame) => OnVideoFrameReady(player, generation);
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
        if (generation != _sourceGeneration || player != _videoPlayer) return;
        _videoFrameReady = false;
        UpdateInputImage();
        player.Play();
        SetStatus($"Video ready · {_sources[_currentSourceIndex].Location}");
    }

    void OnVideoError(VideoPlayer player, string message, int generation)
    {
        if (generation != _sourceGeneration || player != _videoPlayer) return;
        SetSourceError($"Video decode failed: {message}");
    }

    void OnVideoFrameReady(VideoPlayer player, int generation)
    {
        if (generation != _sourceGeneration || player != _videoPlayer) return;
        _videoFrameReady = true;
        _cameraImage?.MarkDirtyRepaint();
    }

    void OnVideoLoopPoint(VideoPlayer player, int generation)
    {
        if (generation != _sourceGeneration || player != _videoPlayer) return;
        if (_loopCoroutine != null) StopCoroutine(_loopCoroutine);
        _loopCoroutine = StartCoroutine(RestartVideoLoop(generation));
    }

    IEnumerator RestartVideoLoop(int generation)
    {
        _videoPlayer.Pause();
        _videoFrameReady = false;
        while (_readbackPending) yield return null;
        if (generation != _sourceGeneration) yield break;

        yield return ResetInferenceState(generation);
        if (generation != _sourceGeneration) yield break;
        ClearDisplayTextures();
        _videoPlayer.frame = 0;
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

        DetachVideoEvents();
        if (_videoPlayer != null) _videoPlayer.Stop();
        _videoFrameReady = false;
        _currentSourceIndex = -1;
        UpdateInputImage();
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

    bool TryGetSourceFrame(
        out Texture texture,
        out int width,
        out int height,
        out int rotation,
        out bool mirrorY
    )
    {
        texture = null;
        width = 0;
        height = 0;
        rotation = 0;
        mirrorY = false;
        if (_currentSourceIndex < 0 || _currentSourceIndex >= _sources.Count) return false;

        if (_sources[_currentSourceIndex].Kind == InputSourceKind.Camera)
        {
            if (_webcam == null || !_webcam.isPlaying || !_webcam.didUpdateThisFrame ||
                _webcam.width <= 16 || _webcam.height <= 16)
                return false;
            texture = _webcam;
            width = _webcam.width;
            height = _webcam.height;
            rotation = _webcam.videoRotationAngle;
            mirrorY = _webcam.videoVerticallyMirrored;
        }
        else
        {
            texture = _videoPlayer.texture;
            if (!_videoFrameReady || texture == null || texture.width <= 0 || texture.height <= 0)
                return false;
            width = texture.width;
            height = texture.height;
            _videoFrameReady = false;
        }

        _cameraImage?.MarkDirtyRepaint();
        return true;
    }

    IEnumerator ResetInferenceState(int generation)
    {
        if (_plugin == IntPtr.Zero) yield break;
        while (_alphaLeases.Count > 0)
        {
            ReleaseCompletedAlphaSlots();
            if (_alphaLeases.Count == 0) break;
            yield return null;
            if (generation != _sourceGeneration) yield break;
        }
        ReleaseAllAlphaSlots();
        RVMNative.RVMResetState(_plugin);
    }

    void SetSourceError(string message)
    {
        _sourceError = message;
        SetStatus(message);
    }

    void UpdateInputImage()
    {
        if (_cameraImage == null) return;
        var preprocessed = _inputTexture != null;
        Texture source = null;
        if (_currentSourceIndex >= 0 && _currentSourceIndex < _sources.Count)
            source = _sources[_currentSourceIndex].Kind == InputSourceKind.Camera ?
                _webcam : _videoPlayer.texture;
        _cameraImage.image = preprocessed ? _inputTexture : source;
        _cameraImage.uv = preprocessed ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1);
        _cameraImage.MarkDirtyRepaint();
    }

    void ClearDisplayTextures()
    {
        ClearTexture(_inputTexture);
        ClearTexture(_alphaDisplayTexture);
        _cameraImage?.MarkDirtyRepaint();
        _alphaImage?.MarkDirtyRepaint();
    }

    static void ClearTexture(RenderTexture texture)
    {
        if (texture == null) return;
        var previous = RenderTexture.active;
        RenderTexture.active = texture;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = previous;
    }
}

} // namespace RVM
