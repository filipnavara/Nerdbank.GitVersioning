﻿#nullable enable

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace Nerdbank.GitVersioning.ManagedGit
{
    internal static class GitPackReader
    {
        private static readonly byte[] Signature = GitRepository.Encoding.GetBytes("PACK");

        public static Stream GetObject(GitPack pack, Stream stream, long offset, string objectType, GitPackObjectType packObjectType)
        {
            if (pack == null)
            {
                throw new ArgumentNullException(nameof(pack));
            }

            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var (type, decompressedSize) = ReadObjectHeader(stream);

            if (type == GitPackObjectType.OBJ_OFS_DELTA)
            {
                var baseObjectRelativeOffset = ReadVariableLengthInteger(stream);
                var baseObjectOffset = (int)(offset - baseObjectRelativeOffset);

                var deltaStream = new ZLibStream(stream, decompressedSize);
                var baseObjectStream = pack.GetObject(baseObjectOffset, objectType);

                return new GitPackDeltafiedStream(baseObjectStream, deltaStream);
            }
            else if (type == GitPackObjectType.OBJ_REF_DELTA)
            {
                Span<byte> baseObjectId = stackalloc byte[20];
                stream.ReadAll(baseObjectId);

                Stream baseObject = pack.GetObjectFromRepository(GitObjectId.Parse(baseObjectId), objectType)!;
                var seekableBaseObject = new GitPackMemoryCacheStream(baseObject);

                var deltaStream = new ZLibStream(stream, decompressedSize);

                return new GitPackDeltafiedStream(seekableBaseObject, deltaStream);
            }

            // Tips for handling deltas: https://github.com/choffmeister/gitnet/blob/4d907623d5ce2d79a8875aee82e718c12a8aad0b/src/GitNet/GitPack.cs
            if (type != packObjectType)
            {
                throw new GitException($"An object of type {objectType} could not be located at offset {offset}.") { ErrorCode = GitException.ErrorCodes.ObjectNotFound };
            }

            return new ZLibStream(stream, decompressedSize);
        }

        public static (GitPackObjectType, long) ReadObjectHeader(Stream stream)
        {
            Span<byte> value = stackalloc byte[1];
            stream.Read(value);

            var type = (GitPackObjectType)((value[0] & 0b0111_0000) >> 4);
            long length = value[0] & 0b_1111;

            if ((value[0] & 0b1000_0000) == 0)
            {
                return (type, length);
            }

            int shift = 4;

            do
            {
                stream.Read(value);
                length = length | ((value[0] & (long)0b0111_1111) << shift);
                shift += 7;
            } while ((value[0] & 0b1000_0000) != 0);

            return (type, length);
        }

        private static int ReadVariableLengthInteger(Stream stream)
        {
            int offset = -1;
            int b;

            do
            {
                offset++;
                b = stream.ReadByte();
                offset = (offset << 7) + (b & 127);
            }
            while ((b & (byte)128) != 0);

            return offset;
        }
    }
}
