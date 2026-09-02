using System.Buffers.Binary;

namespace WorldGen.Server;

/// <summary>
/// Small, versioned command stream for data that is already shaped for GPU upload.
/// It deliberately contains no names or JSON: semantic/UI state travels separately.
/// </summary>
internal static class GpuRenderPacket
{
    private const int HeaderBytes = 16;
    private const int DescriptorBytes = 32;
    private const ushort Version = 2;
    private const byte Texture2DArray = 1;
    private const byte Texture2DArrayPatch = 2;
    private const byte Terrain = 1;
    private const byte Water = 2;
    private const byte Elevation = 3;
    private const byte Rgba8 = 1;
    private const byte Rg8 = 2;
    private const byte R32F = 3;

    public static byte[] Surface(byte[] terrain, byte[] water, byte[] elevation, int faceSize, uint revision = 0)
    {
        ArgumentNullException.ThrowIfNull(terrain); ArgumentNullException.ThrowIfNull(water); ArgumentNullException.ThrowIfNull(elevation);
        if (faceSize is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(faceSize));

        var facePixels = checked(faceSize * faceSize);
        var terrainBytes = checked(facePixels * 6 * 4); var waterBytes = checked(facePixels * 6 * 2); var elevationBytes = checked(facePixels * 6 * 4);
        if (terrain.Length != terrainBytes || water.Length != waterBytes || elevation.Length != elevationBytes)
            throw new ArgumentException("Surface payload does not match the declared cubed-sphere size.");

        var commandCount = 3;
        var payloadOffset = HeaderBytes + DescriptorBytes * commandCount;
        var packet = new byte[checked(payloadOffset + terrainBytes + waterBytes + elevationBytes)];
        var span = packet.AsSpan();
        "WGRP"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], DescriptorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)commandCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)packet.Length);

        WriteTextureArray(span.Slice(HeaderBytes, DescriptorBytes), Texture2DArray, Terrain, Rgba8, faceSize, faceSize, 6, 0, 0, 0, payloadOffset, terrainBytes, revision);
        WriteTextureArray(span.Slice(HeaderBytes + DescriptorBytes, DescriptorBytes), Texture2DArray, Water, Rg8, faceSize, faceSize, 6, 0, 0, 0, payloadOffset + terrainBytes, waterBytes, revision);
        WriteTextureArray(span.Slice(HeaderBytes + DescriptorBytes * 2, DescriptorBytes), Texture2DArray, Elevation, R32F, faceSize, faceSize, 6, 0, 0, 0, payloadOffset + terrainBytes + waterBytes, elevationBytes, revision);
        terrain.CopyTo(span[payloadOffset..]); water.CopyTo(span[(payloadOffset + terrainBytes)..]); elevation.CopyTo(span[(payloadOffset + terrainBytes + waterBytes)..]);
        return packet;
    }

    public static byte[] TerrainPatches(IReadOnlyList<GpuTexturePatch> patches, uint revision)
    {
        ArgumentNullException.ThrowIfNull(patches);
        if (patches.Count == 0) return [];
        var payloadBytes = patches.Sum(patch => checked(patch.Width * patch.Height * 4));
        var payloadOffset = checked(HeaderBytes + DescriptorBytes * patches.Count);
        var packet = new byte[checked(payloadOffset + payloadBytes)];
        var span = packet.AsSpan();
        "WGRP"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], DescriptorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)patches.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)packet.Length);
        var cursor = payloadOffset;
        for (var index = 0; index < patches.Count; index++)
        {
            var patch = patches[index];
            if (patch.Layer is < 0 or >= 6 || patch.X < 0 || patch.Y < 0 || patch.Width <= 0 || patch.Height <= 0 ||
                patch.X + patch.Width > ushort.MaxValue || patch.Y + patch.Height > ushort.MaxValue ||
                patch.Pixels.Length != patch.Width * patch.Height * 4)
                throw new ArgumentException("Invalid terrain texture patch.", nameof(patches));
            WriteTextureArray(span.Slice(HeaderBytes + index * DescriptorBytes, DescriptorBytes), Texture2DArrayPatch,
                Terrain, Rgba8, patch.Width, patch.Height, 1, patch.X, patch.Y, patch.Layer, cursor, patch.Pixels.Length, revision);
            patch.Pixels.CopyTo(span[cursor..]); cursor += patch.Pixels.Length;
        }
        return packet;
    }

    public static byte[] SurfacePatches(IReadOnlyList<GpuTexturePatch> terrain, IReadOnlyList<GpuTexturePatch> water,
        IReadOnlyList<GpuFloatTexturePatch> elevation, uint revision)
    {
        var commands = terrain.Select(p => (Patch: p, Resource: Terrain, Format: Rgba8, Bytes: 4))
            .Concat(water.Select(p => (Patch: p, Resource: Water, Format: Rg8, Bytes: 2)))
            .Concat(elevation.Select(p => (Patch: new GpuTexturePatch(p.Layer, p.X, p.Y, p.Width, p.Height,
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(p.Pixels.AsSpan()).ToArray()), Resource: Elevation, Format: R32F, Bytes: 4))).ToArray();
        if (commands.Length == 0) return [];
        var payloadOffset = checked(HeaderBytes + DescriptorBytes * commands.Length); var payloadBytes = commands.Sum(c => c.Patch.Pixels.Length);
        var packet = new byte[checked(payloadOffset + payloadBytes)]; var span = packet.AsSpan();
        "WGRP"u8.CopyTo(span); BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version); BinaryPrimitives.WriteUInt16LittleEndian(span[6..], DescriptorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)commands.Length); BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)packet.Length);
        var cursor = payloadOffset;
        for (var index = 0; index < commands.Length; index++)
        {
            var command = commands[index]; var patch = command.Patch;
            if (patch.Layer is < 0 or >= 6 || patch.X < 0 || patch.Y < 0 || patch.Width <= 0 || patch.Height <= 0 || patch.Pixels.Length != patch.Width * patch.Height * command.Bytes)
                throw new ArgumentException("Invalid surface texture patch.");
            WriteTextureArray(span.Slice(HeaderBytes + index * DescriptorBytes, DescriptorBytes), Texture2DArrayPatch,
                command.Resource, command.Format, patch.Width, patch.Height, 1, patch.X, patch.Y, patch.Layer, cursor, patch.Pixels.Length, revision);
            patch.Pixels.CopyTo(span[cursor..]); cursor += patch.Pixels.Length;
        }
        return packet;
    }

    private static void WriteTextureArray(Span<byte> descriptor, byte opcode, byte resource, byte format,
        int width, int height, int layers, int x, int y, int layer, int offset, int length, uint revision)
    {
        descriptor[0] = opcode;
        descriptor[1] = resource;
        descriptor[2] = format;
        descriptor[3] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[4..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[6..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[8..], (ushort)layers);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[10..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], revision);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[24..], (ushort)x);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[26..], (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[28..], (ushort)layer);
    }
}

internal sealed record GpuTexturePatch(int Layer, int X, int Y, int Width, int Height, byte[] Pixels);
internal sealed record GpuFloatTexturePatch(int Layer, int X, int Y, int Width, int Height, float[] Pixels);
