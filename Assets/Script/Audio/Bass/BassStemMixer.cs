﻿using System;
using System.IO;
using ManagedBass;
using ManagedBass.Mix;
using UnityEngine;
using YARG.Core.Audio;
using YARG.Core.Logging;
using YARG.Core.Song;

namespace YARG.Audio.BASS
{
    public sealed class BassStemMixer : StemMixer
    {
        private readonly int _mixerHandle;
        private readonly int _sourceStream;

        private StreamHandle _mainHandle;
        private int _songEndHandle;

        public override event Action SongEnd
        {
            add
            {
                if (_songEndHandle == 0)
                {
                    void sync(int _, int __, int ___, IntPtr _____)
                    {
                        // Prevent potential race conditions by caching the value as a local
                        var end = _songEnd;
                        if (end != null)
                        {
                            UnityMainThreadCallback.QueueEvent(end.Invoke);
                        }
                    }
                    _songEndHandle = BassMix.ChannelSetSync(_mainHandle.Stream, SyncFlags.End, 0, sync);
                }

                _songEnd += value;
            }
            remove
            {
                _songEnd -= value;
            }
        }

        internal BassStemMixer(string name, BassAudioManager manager, float speed, double volume, int handle, int sourceStream, bool clampStemVolume)
            : base(name, manager, speed, clampStemVolume)
        {
            _mixerHandle = handle;
            _sourceStream = sourceStream;
            SetVolume_Internal(volume);
        }

        protected override int Play_Internal(bool restart)
        {
            if (IsPaused && !Bass.ChannelPlay(_mixerHandle, restart))
            {
                return (int) Bass.LastError;
            }
            return 0;
        }

        protected override void FadeIn_Internal(float maxVolume, double duration)
        {
            Bass.ChannelSlideAttribute(_mixerHandle, ChannelAttribute.Volume, maxVolume, (int) (duration * SongEntry.MILLISECOND_FACTOR));
        }

        protected override void FadeOut_Internal(double duration)
        {
            Bass.ChannelSlideAttribute(_mixerHandle, ChannelAttribute.Volume, 0, (int) (duration * SongEntry.MILLISECOND_FACTOR));
        }

        protected override int Pause_Internal()
        {
            if (!IsPaused && !Bass.ChannelPause(_mixerHandle))
            {
                return (int) Bass.LastError;
            }
            return 0;
        }

        protected override double GetPosition_Internal()
        {
            long position = Bass.ChannelGetPosition(_mainHandle.Stream);
            if (position < 0)
            {
                YargLogger.LogFormatError("Failed to get channel position in bytes: {0}", Bass.LastError);
                return -1;
            }

            double seconds = Bass.ChannelBytes2Seconds(_mainHandle.Stream, position);
            if (seconds < 0)
            {
                YargLogger.LogFormatError("Failed to get channel position in seconds: {0}", Bass.LastError);
                return -1;
            }
            return seconds;
        }

        protected override double GetVolume_Internal()
        {
            if (!Bass.ChannelGetAttribute(_mixerHandle, ChannelAttribute.Volume, out float volume))
            {
                YargLogger.LogFormatError("Failed to get volume: {0}", Bass.LastError);
            }
            return volume;
        }

        protected override void SetPosition_Internal(double position)
        {
            bool playing = !IsPaused;
            if (playing)
            {
                // Pause when seeking to avoid desyncing individual stems
                Pause_Internal();
            }

            if (_channels.Count == 0)
            {
                long bytes = Bass.ChannelSeconds2Bytes(_mainHandle.Stream, position);
                if (bytes < 0)
                {
                    YargLogger.LogFormatError("Failed to get channel position in bytes: {0}!", Bass.LastError);
                }
                else if (!BassMix.ChannelSetPosition(_mainHandle.Stream, bytes, PositionFlags.Bytes | PositionFlags.MixerReset))
                {
                    YargLogger.LogFormatError("Failed to set channel position: {0}!", Bass.LastError);
                }
            }
            else
            {
                if (_sourceStream != 0)
                {
                    BassMix.SplitStreamReset(_sourceStream);
                }

                foreach (var channel in _channels)
                {
                    channel.SetPosition(position);
                }
            }

            if (playing)
            {
                if (!Bass.ChannelUpdate(_mixerHandle, BassHelpers.PLAYBACK_BUFFER_LENGTH))
                {
                    YargLogger.LogFormatError("Failed to set update channel: {0}!", Bass.LastError);
                }
                Play_Internal(false);
            }
        }

        protected override void SetVolume_Internal(double volume)
        {
            if (!Bass.ChannelSetAttribute(_mixerHandle, ChannelAttribute.Volume, volume))
            {
                YargLogger.LogFormatError("Failed to set mixer volume: {0}", Bass.LastError);
            }
        }

        protected override int GetData_Internal(float[] buffer)
        {
            int data = Bass.ChannelGetData(_mixerHandle, buffer, (int) (DataFlags.FFT256));
            if (data < 0)
            {
                return (int) Bass.LastError;
            }
            return data;
        }

        protected override void SetSpeed_Internal(float speed, bool shiftPitch)
        {
            speed = (float) Math.Clamp(speed, 0.05, 50);
            if (_speed == speed)
            {
                return;
            }

            _speed = speed;
            foreach (var channel in _channels)
            {
                channel.SetSpeed(speed, shiftPitch);
            }
        }

        protected override bool AddChannel_Internal(SongStem stem)
        {
            _mainHandle = StreamHandle.Create(_sourceStream, null);
            if (_mainHandle == null)
            {
                YargLogger.LogFormatError("Failed to load stem split stream {stem}: {0}!", Bass.LastError);
            }

            if (!BassMix.MixerAddChannel(_mixerHandle, _mainHandle.Stream, BassFlags.Default))
            {
                YargLogger.LogFormatError("Failed to add channel {stem} to mixer: {0}!", Bass.LastError);
                return false;
            }
            _length = BassAudioManager.GetLengthInSeconds(_sourceStream);
            return true;
        }

        protected override bool AddChannel_Internal(SongStem stem, Stream stream)
        {
            if (!BassAudioManager.CreateSourceStream(stream, out int sourceStream))
            {
                YargLogger.LogFormatError("Failed to load stem source stream {stem}: {0}!", Bass.LastError);
                return false;
            }

            if (!BassAudioManager.CreateSplitStreams(sourceStream, null, out var streamHandles, out var reverbHandles))
            {
                YargLogger.LogFormatError("Failed to load stem split streams {stem}: {0}!", Bass.LastError);
                return false;
            }

            if (!BassMix.MixerAddChannel(_mixerHandle, streamHandles.Stream, BassFlags.Default) ||
                !BassMix.MixerAddChannel(_mixerHandle, reverbHandles.Stream, BassFlags.Default))
            {
                YargLogger.LogFormatError("Failed to add channel {stem} to mixer: {0}!", Bass.LastError);
                return false;
            }

            CreateChannel(stem, sourceStream, streamHandles, reverbHandles);
            return true;
        }

        protected override bool AddChannel_Internal(SongStem stem, int[] indices, float[] panning)
        {
            if (!BassAudioManager.CreateSplitStreams(_sourceStream, indices, out var streamHandles, out var reverbHandles))
            {
                YargLogger.LogFormatError("Failed to load stem {stem}: {0}!", Bass.LastError);
                return false;
            }

            if (!BassMix.MixerAddChannel(_mixerHandle, streamHandles.Stream, BassFlags.MixerChanMatrix | BassFlags.MixerChanDownMix) ||
                !BassMix.MixerAddChannel(_mixerHandle, reverbHandles.Stream, BassFlags.MixerChanMatrix | BassFlags.MixerChanDownMix))
            {
                YargLogger.LogFormatError("Failed to add channel {stem} to mixer: {0}!", Bass.LastError);
                return false;
            }

            // First array = left pan, second = right pan
            float[,] volumeMatrix = new float[2, indices.Length];

            const int LEFT_PAN = 0;
            const int RIGHT_PAN = 1;
            for (int i = 0; i < indices.Length; ++i)
            {
                volumeMatrix[LEFT_PAN, i] = panning[2 * i];
            }

            for (int i = 0; i < indices.Length; ++i)
            {
                volumeMatrix[RIGHT_PAN, i] = panning[2 * i + 1];
            }

            if (!BassMix.ChannelSetMatrix(streamHandles.Stream, volumeMatrix) ||
                !BassMix.ChannelSetMatrix(reverbHandles.Stream, volumeMatrix))
            {
                YargLogger.LogFormatError("Failed to set {stem} matrices: {0}!", Bass.LastError);
                return false;
            }

            CreateChannel(stem, 0, streamHandles, reverbHandles);
            return true;
        }

        protected override bool RemoveChannel_Internal(SongStem stemToRemove)
        {
            int index = _channels.FindIndex(channel => channel.Stem == stemToRemove);
            if (index == -1)
            {
                return false;
            }
            _channels[index].Dispose();
            _channels.RemoveAt(index);
            return true;
        }

        protected override void DisposeManagedResources()
        {
            if (_channels.Count == 0)
            {
                _mainHandle.Dispose();
                return;
            }

            foreach (var channel in Channels)
            {
                channel.Dispose();
            }
        }

        protected override void DisposeUnmanagedResources()
        {
            if (_mixerHandle != 0)
            {
                if (!Bass.StreamFree(_mixerHandle))
                {
                    YargLogger.LogFormatError("Failed to free mixer stream (THIS WILL LEAK MEMORY!): {0}!", Bass.LastError);
                }
            }

            if (_sourceStream != 0)
            {
                if (!Bass.StreamFree(_sourceStream))
                {
                    YargLogger.LogFormatError("Failed to free mixer source stream (THIS WILL LEAK MEMORY!): {0}!", Bass.LastError);
                }
            }
        }

        private void CreateChannel(SongStem stem, int sourceStream, StreamHandle streamHandles, StreamHandle reverbHandles)
        {
            var pitchparams = BassAudioManager.SetPitchParams(stem, _speed, streamHandles, reverbHandles);
            var stemchannel = new BassStemChannel(_manager, stem, _clampStemVolume, sourceStream, pitchparams, streamHandles, reverbHandles);

            double length = BassAudioManager.GetLengthInSeconds(streamHandles.Stream);
            if (_mainHandle == null || length > _length)
            {
                _mainHandle = streamHandles;
                _length = length;
            }

            _channels.Add(stemchannel);
        }
    }
}