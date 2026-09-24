using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ErrorCode = OpenTK.Graphics.OpenGL.ErrorCode;

namespace VintageStoryHDR.Interop;

/// <summary>
/// <c>GL_EXT_memory_object_fd</c> and <c>GL_EXT_semaphore_fd</c>: GL textures backed by memory
/// Vulkan allocated, and semaphores shared with Vulkan, both passed as file descriptors.
/// OpenTK does not bind these, so the entry points come from <c>glfwGetProcAddress</c>; a GL
/// context must be current when this is constructed and whenever it is used.
/// </summary>
internal sealed unsafe class GlExternalObjects
{
    internal const uint TextureTiling = 0x9580;         // GL_TEXTURE_TILING_EXT
    internal const uint DedicatedMemoryObject = 0x9581; // GL_DEDICATED_MEMORY_OBJECT_EXT
    internal const uint OptimalTiling = 0x9584;         // GL_OPTIMAL_TILING_EXT
    internal const uint HandleTypeOpaqueFd = 0x9586;    // GL_HANDLE_TYPE_OPAQUE_FD_EXT
    internal const uint DeviceUuid = 0x9597;            // GL_DEVICE_UUID_EXT
    internal const uint LayoutTransferSrc = 0x9592;     // GL_LAYOUT_TRANSFER_SRC_EXT
    internal const uint Rgb10A2 = 0x8059;               // GL_RGB10_A2

    private static readonly string[] RequiredExtensions =
    {
        "GL_EXT_memory_object",
        "GL_EXT_memory_object_fd",
        "GL_EXT_semaphore",
        "GL_EXT_semaphore_fd",
    };

    internal GlExternalObjects()
    {
        HashSet<string> extensions = new(StringComparer.Ordinal);
        int count = GL.GetInteger(GetPName.NumExtensions);
        for (int i = 0; i < count; i++)
        {
            extensions.Add(GL.GetString(StringNameIndexed.Extensions, i));
        }

        foreach (string name in RequiredExtensions)
        {
            if (!extensions.Contains(name))
            {
                throw new HdrUnavailableException($"The OpenGL driver does not implement {name}, which sharing frames with Vulkan needs.");
            }
        }

        CreateMemoryObjects = (delegate* unmanaged<int, uint*, void>)Resolve("glCreateMemoryObjectsEXT"u8);
        DeleteMemoryObjects = (delegate* unmanaged<int, uint*, void>)Resolve("glDeleteMemoryObjectsEXT"u8);
        MemoryObjectParameteriv = (delegate* unmanaged<uint, uint, int*, void>)Resolve("glMemoryObjectParameterivEXT"u8);
        ImportMemoryFd = (delegate* unmanaged<uint, ulong, uint, int, void>)Resolve("glImportMemoryFdEXT"u8);
        TexStorageMem2D = (delegate* unmanaged<uint, int, uint, int, int, uint, ulong, void>)Resolve("glTexStorageMem2DEXT"u8);
        GenSemaphores = (delegate* unmanaged<int, uint*, void>)Resolve("glGenSemaphoresEXT"u8);
        DeleteSemaphores = (delegate* unmanaged<int, uint*, void>)Resolve("glDeleteSemaphoresEXT"u8);
        ImportSemaphoreFd = (delegate* unmanaged<uint, uint, int, void>)Resolve("glImportSemaphoreFdEXT"u8);
        WaitSemaphore = (delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void>)Resolve("glWaitSemaphoreEXT"u8);
        SignalSemaphore = (delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void>)Resolve("glSignalSemaphoreEXT"u8);
        GetUnsignedBytei = (delegate* unmanaged<uint, uint, byte*, void>)Resolve("glGetUnsignedBytei_vEXT"u8);
    }

    internal delegate* unmanaged<int, uint*, void> CreateMemoryObjects { get; }

    internal delegate* unmanaged<int, uint*, void> DeleteMemoryObjects { get; }

    internal delegate* unmanaged<uint, uint, int*, void> MemoryObjectParameteriv { get; }

    internal delegate* unmanaged<uint, ulong, uint, int, void> ImportMemoryFd { get; }

    internal delegate* unmanaged<uint, int, uint, int, int, uint, ulong, void> TexStorageMem2D { get; }

    internal delegate* unmanaged<int, uint*, void> GenSemaphores { get; }

    internal delegate* unmanaged<int, uint*, void> DeleteSemaphores { get; }

    internal delegate* unmanaged<uint, uint, int, void> ImportSemaphoreFd { get; }

    /// <summary>(semaphore, numBufferBarriers, buffers, numTextureBarriers, textures, srcLayouts)</summary>
    internal delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void> WaitSemaphore { get; }

    /// <summary>(semaphore, numBufferBarriers, buffers, numTextureBarriers, textures, dstLayouts)</summary>
    internal delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void> SignalSemaphore { get; }

    /// <summary>(target, index, data): EXT_external_objects defines DEVICE_UUID_EXT as an indexed query.</summary>
    internal delegate* unmanaged<uint, uint, byte*, void> GetUnsignedBytei { get; }

    /// <summary>The UUID of the device the current GL context runs on, to find the same device in Vulkan.</summary>
    internal Guid DeviceUuidOfContext()
    {
        byte* uuid = stackalloc byte[16];
        new Span<byte>(uuid, 16).Clear();
        GlErrors.DrainPending();
        GetUnsignedBytei(DeviceUuid, 0, uuid);
        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            throw new HdrUnavailableException($"The OpenGL driver did not report its device UUID ({error}).");
        }

        return new Guid(new ReadOnlySpan<byte>(uuid, 16));
    }

    private static nint Resolve(ReadOnlySpan<byte> name)
    {
        nint address;
        fixed (byte* p = name)
        {
            // UTF-8 literals carry a terminating NUL past their length.
            address = GLFW.GetProcAddressRaw(p);
        }

        if (address == 0)
        {
            throw new HdrUnavailableException($"The OpenGL driver does not export {System.Text.Encoding.UTF8.GetString(name)}.");
        }

        return address;
    }
}
