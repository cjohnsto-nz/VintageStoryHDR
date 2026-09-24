using System.Runtime.InteropServices;

namespace VintageStoryHDR.Interop;

// Vulkan structures and the two loader exports this mod calls directly; every other entry
// point is resolved through vkGetInstanceProcAddr / vkGetDeviceProcAddr. Layouts follow
// vulkan_core.h on x86-64 (sequential, natural alignment).
#pragma warning disable CA1051 // Interop structs expose fields, as the C structs do.

internal static unsafe partial class VulkanNative
{
    internal const string Library = "libvulkan.so.1";

    [LibraryImport(Library)]
    internal static partial nint vkGetInstanceProcAddr(nint instance, byte* name);

    [LibraryImport(Library)]
    internal static partial int vkEnumerateInstanceExtensionProperties(byte* layerName, uint* count, byte* properties);
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkApplicationInfo
{
    public int SType;
    public nint PNext;
    public nint PApplicationName;
    public uint ApplicationVersion;
    public nint PEngineName;
    public uint EngineVersion;
    public uint ApiVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkInstanceCreateInfo
{
    public int SType;
    public nint PNext;
    public uint Flags;
    public nint PApplicationInfo;
    public uint EnabledLayerCount;
    public nint PpEnabledLayerNames;
    public uint EnabledExtensionCount;
    public nint PpEnabledExtensionNames;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkWaylandSurfaceCreateInfoKHR
{
    public int SType;
    public nint PNext;
    public uint Flags;
    public nint Display;
    public nint Surface;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkSurfaceFormatKHR
{
    public int Format;
    public int ColorSpace;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkSurfaceCapabilitiesKHR
{
    public uint MinImageCount;
    public uint MaxImageCount;
    public uint CurrentWidth;
    public uint CurrentHeight;
    public uint MinWidth;
    public uint MinHeight;
    public uint MaxWidth;
    public uint MaxHeight;
    public uint MaxImageArrayLayers;
    public uint SupportedTransforms;
    public uint CurrentTransform;
    public uint SupportedCompositeAlpha;
    public uint SupportedUsageFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkDeviceQueueCreateInfo
{
    public int SType;
    public nint PNext;
    public uint Flags;
    public uint QueueFamilyIndex;
    public uint QueueCount;
    public nint PQueuePriorities;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkDeviceCreateInfo
{
    public int SType;
    public nint PNext;
    public uint Flags;
    public uint QueueCreateInfoCount;
    public nint PQueueCreateInfos;
    public uint EnabledLayerCount;
    public nint PpEnabledLayerNames;
    public uint EnabledExtensionCount;
    public nint PpEnabledExtensionNames;
    public nint PEnabledFeatures;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkSwapchainCreateInfoKHR
{
    public int SType;
    public nint PNext;
    public uint Flags;
    public ulong Surface;
    public uint MinImageCount;
    public int ImageFormat;
    public int ImageColorSpace;
    public uint ImageWidth;
    public uint ImageHeight;
    public uint ImageArrayLayers;
    public uint ImageUsage;
    public int ImageSharingMode;
    public uint QueueFamilyIndexCount;
    public nint PQueueFamilyIndices;
    public uint PreTransform;
    public uint CompositeAlpha;
    public int PresentMode;
    public uint Clipped;
    public ulong OldSwapchain;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkImageCreateInfo
{
    public int SType;
    public nint PNext;
    public uint Flags;
    public int ImageType;
    public int Format;
    public uint Width;
    public uint Height;
    public uint Depth;
    public uint MipLevels;
    public uint ArrayLayers;
    public int Samples;
    public int Tiling;
    public uint Usage;
    public int SharingMode;
    public uint QueueFamilyIndexCount;
    public nint PQueueFamilyIndices;
    public int InitialLayout;
}

/// <summary>VkExternalMemoryImageCreateInfo, VkExportMemoryAllocateInfo and VkExportSemaphoreCreateInfo share this shape.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VkHandleTypesInfo
{
    public int SType;
    public nint PNext;
    public uint HandleTypes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkMemoryDedicatedAllocateInfo
{
    public int SType;
    public nint PNext;
    public ulong Image;
    public ulong Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkMemoryAllocateInfo
{
    public int SType;
    public nint PNext;
    public ulong AllocationSize;
    public uint MemoryTypeIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkMemoryRequirements
{
    public ulong Size;
    public ulong Alignment;
    public uint MemoryTypeBits;
}

/// <summary>VkMemoryGetFdInfoKHR and VkSemaphoreGetFdInfoKHR share this shape.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VkGetFdInfo
{
    public int SType;
    public nint PNext;
    public ulong Handle;
    public uint HandleType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkCommandPoolCreateInfo
{
    public int SType;
    public nint PNext;
    public uint Flags;
    public uint QueueFamilyIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkCommandBufferAllocateInfo
{
    public int SType;
    public nint PNext;
    public ulong CommandPool;
    public int Level;
    public uint CommandBufferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkCommandBufferBeginInfo
{
    public int SType;
    public nint PNext;
    public uint Flags;
    public nint PInheritanceInfo;
}

/// <summary>VkSemaphoreCreateInfo and VkFenceCreateInfo share this shape.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VkFlagsCreateInfo
{
    public int SType;
    public nint PNext;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkImageSubresourceRange
{
    public uint AspectMask;
    public uint BaseMipLevel;
    public uint LevelCount;
    public uint BaseArrayLayer;
    public uint LayerCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkImageMemoryBarrier
{
    public int SType;
    public nint PNext;
    public uint SrcAccessMask;
    public uint DstAccessMask;
    public int OldLayout;
    public int NewLayout;
    public uint SrcQueueFamilyIndex;
    public uint DstQueueFamilyIndex;
    public ulong Image;
    public VkImageSubresourceRange SubresourceRange;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkImageSubresourceLayers
{
    public uint AspectMask;
    public uint MipLevel;
    public uint BaseArrayLayer;
    public uint LayerCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkImageBlit
{
    public VkImageSubresourceLayers SrcSubresource;
    public int SrcX0;
    public int SrcY0;
    public int SrcZ0;
    public int SrcX1;
    public int SrcY1;
    public int SrcZ1;
    public VkImageSubresourceLayers DstSubresource;
    public int DstX0;
    public int DstY0;
    public int DstZ0;
    public int DstX1;
    public int DstY1;
    public int DstZ1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkSubmitInfo
{
    public int SType;
    public nint PNext;
    public uint WaitSemaphoreCount;
    public nint PWaitSemaphores;
    public nint PWaitDstStageMask;
    public uint CommandBufferCount;
    public nint PCommandBuffers;
    public uint SignalSemaphoreCount;
    public nint PSignalSemaphores;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkPresentInfoKHR
{
    public int SType;
    public nint PNext;
    public uint WaitSemaphoreCount;
    public nint PWaitSemaphores;
    public uint SwapchainCount;
    public nint PSwapchains;
    public nint PImageIndices;
    public nint PResults;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VkHdrMetadataEXT
{
    public int SType;
    public nint PNext;
    public float RedX;
    public float RedY;
    public float GreenX;
    public float GreenY;
    public float BlueX;
    public float BlueY;
    public float WhiteX;
    public float WhiteY;
    public float MaxLuminance;
    public float MinLuminance;
    public float MaxContentLightLevel;
    public float MaxFrameAverageLightLevel;
}

#pragma warning restore CA1051
