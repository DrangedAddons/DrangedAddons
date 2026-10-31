// The Vulkan backend: an implicit Vulkan layer. The loader only loads it for a game the manager started (the manifest's
// enable_environment), so there's no injection. It keeps each swap chain's images, and on each present draws the
// overlay into the image being presented, on the presenting queue, after the game's own work (its wait semaphores)
// and before the present (ours).
#include "overlay.h"
#include "imgui.h"
#include "imgui_impl_vulkan.h"
#include <vulkan/vk_layer.h>
#undef CreateSemaphore // windows.h's macro, not Vulkan's function

#include <cmath>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace {

template <class T> void* Key(T handle) { return *(void**)handle; } // the loader's dispatch pointer

struct Instance
{
    VkInstance handle = VK_NULL_HANDLE;
    uint32_t api = VK_API_VERSION_1_0;
    PFN_vkGetInstanceProcAddr next = nullptr;
    PFN_vkDestroyInstance DestroyInstance = nullptr;
    PFN_vkCreateWin32SurfaceKHR CreateWin32SurfaceKHR = nullptr;
    PFN_vkDestroySurfaceKHR DestroySurfaceKHR = nullptr;
    PFN_vkGetPhysicalDeviceQueueFamilyProperties GetPhysicalDeviceQueueFamilyProperties = nullptr;
    PFN_vkGetPhysicalDeviceMemoryProperties GetPhysicalDeviceMemoryProperties = nullptr;
    std::unordered_map<VkSurfaceKHR, HWND> windows;
};

struct Device
{
    VkDevice handle = VK_NULL_HANDLE;
    VkPhysicalDevice physical = VK_NULL_HANDLE;
    Instance* instance = nullptr;
    PFN_vkGetDeviceProcAddr next = nullptr;
    PFN_vkSetDeviceLoaderData setLoaderData = nullptr;
    std::vector<VkQueueFamilyProperties> families;
    std::unordered_map<VkQueue, uint32_t> queueFamily;
    bool imgui = false;           // ImGui's Vulkan renderer is set up on this device
    VkRenderPass imguiPass = VK_NULL_HANDLE; // the render pass its pipeline was made for
    VkFormat imguiFormat = VK_FORMAT_UNDEFINED;

#define F(name) PFN_vk##name name = nullptr;
    F(DestroyDevice) F(GetDeviceQueue) F(GetDeviceQueue2) F(CreateSwapchainKHR) F(DestroySwapchainKHR) F(GetSwapchainImagesKHR)
    F(QueuePresentKHR) F(QueueSubmit) F(DeviceWaitIdle) F(CreateImageView) F(DestroyImageView) F(CreateFramebuffer)
    F(DestroyFramebuffer) F(CreateRenderPass) F(DestroyRenderPass) F(CreateCommandPool) F(DestroyCommandPool)
    F(AllocateCommandBuffers) F(ResetCommandBuffer) F(BeginCommandBuffer) F(EndCommandBuffer) F(CmdBeginRenderPass)
    F(CmdEndRenderPass) F(CmdPipelineBarrier) F(CmdCopyImageToBuffer) F(CreateFence) F(DestroyFence) F(WaitForFences)
    F(ResetFences) F(CreateSemaphore) F(DestroySemaphore) F(CreateBuffer) F(DestroyBuffer) F(GetBufferMemoryRequirements)
    F(AllocateMemory) F(FreeMemory) F(BindBufferMemory) F(MapMemory) F(UnmapMemory)
#undef F
};

struct Swapchain
{
    Device* device = nullptr;
    VkSwapchainKHR handle = VK_NULL_HANDLE;
    HWND window = nullptr;
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkExtent2D extent{};
    uint32_t minImages = 2;
    bool canCopy = false; // made with TRANSFER_SRC (only when a test asked for a frame dump)
    std::vector<VkImage> images;
    std::vector<VkImageView> views;
    std::vector<VkFramebuffer> framebuffers;
    VkRenderPass pass = VK_NULL_HANDLE;
    // Per queue family the overlay has drawn from (almost always one).
    uint32_t family = UINT32_MAX;
    VkCommandPool pool = VK_NULL_HANDLE;
    std::vector<VkCommandBuffer> commands;
    std::vector<VkFence> fences;
    std::vector<VkSemaphore> drawn;
};

std::mutex g_lock;
std::unordered_map<void*, std::unique_ptr<Instance>> g_instances; // by dispatch key (instance and its physical devices)
std::unordered_map<VkPhysicalDevice, Instance*> g_physical;
std::unordered_map<void*, std::unique_ptr<Device>> g_devices;     // by dispatch key (device, its queues and command buffers)
std::unordered_map<VkSwapchainKHR, std::unique_ptr<Swapchain>> g_swapchains;
bool g_dumped = false;
int g_drawnFrames = 0; // the test dump waits a few frames: ImGui shows auto-sized windows from their second frame

bool IsSrgb(VkFormat f)
{
    return f == VK_FORMAT_B8G8R8A8_SRGB || f == VK_FORMAT_R8G8B8A8_SRGB || f == VK_FORMAT_A8B8G8R8_SRGB_PACK32;
}

/// ImGui's colours are sRGB values; an sRGB swap chain treats what's written as linear and encodes it again, which
/// washes them out. Converting the vertex colours to linear first puts them back.
void LinearizeColours(ImDrawData* data)
{
    static uint8_t table[256];
    static bool ready = false;
    if (!ready)
    {
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255.f;
            float l = c <= 0.04045f ? c / 12.92f : powf((c + 0.055f) / 1.055f, 2.4f);
            table[i] = (uint8_t)(l * 255.f + 0.5f);
        }
        ready = true;
    }
    for (auto* list : data->CmdLists)
        for (auto& v : list->VtxBuffer)
        {
            auto c = v.col; // ABGR: alpha stays as it is
            v.col = (c & 0xFF000000) | (ImU32)table[(c >> 16) & 255] << 16 | (ImU32)table[(c >> 8) & 255] << 8 | table[c & 255];
        }
}

Instance* InstanceOf(void* key)
{
    std::lock_guard guard(g_lock);
    auto it = g_instances.find(key);
    return it == g_instances.end() ? nullptr : it->second.get();
}

Device* DeviceOf(void* key)
{
    std::lock_guard guard(g_lock);
    auto it = g_devices.find(key);
    return it == g_devices.end() ? nullptr : it->second.get();
}

// ---- Instance ----

VkResult VKAPI_CALL CreateInstance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks* allocator, VkInstance* out)
{
    auto* link = (VkLayerInstanceCreateInfo*)info->pNext;
    while (link && !(link->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO && link->function == VK_LAYER_LINK_INFO))
        link = (VkLayerInstanceCreateInfo*)link->pNext;
    if (!link) return VK_ERROR_INITIALIZATION_FAILED;
    auto next = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    link->u.pLayerInfo = link->u.pLayerInfo->pNext;
    auto create = (PFN_vkCreateInstance)next(VK_NULL_HANDLE, "vkCreateInstance");
    VkResult result = create(info, allocator, out);
    if (result != VK_SUCCESS) return result;

    auto data = std::make_unique<Instance>();
    data->handle = *out;
    data->next = next;
    if (info->pApplicationInfo && info->pApplicationInfo->apiVersion) data->api = info->pApplicationInfo->apiVersion;
#define F(name) data->name = (PFN_vk##name)next(*out, "vk" #name);
    F(DestroyInstance) F(CreateWin32SurfaceKHR) F(DestroySurfaceKHR) F(GetPhysicalDeviceQueueFamilyProperties) F(GetPhysicalDeviceMemoryProperties)
#undef F
    overlay::SetApi("vulkan");
    overlay::Log("layer loaded (Vulkan %u.%u)", VK_API_VERSION_MAJOR(data->api), VK_API_VERSION_MINOR(data->api));
    std::lock_guard guard(g_lock);
    g_instances[Key(*out)] = std::move(data);
    return VK_SUCCESS;
}

void VKAPI_CALL DestroyInstance(VkInstance instance, const VkAllocationCallbacks* allocator)
{
    auto* data = InstanceOf(Key(instance));
    if (!data) return;
    auto destroy = data->DestroyInstance;
    {
        std::lock_guard guard(g_lock);
        for (auto it = g_physical.begin(); it != g_physical.end();)
            it = it->second == data ? g_physical.erase(it) : std::next(it);
        g_instances.erase(Key(instance));
    }
    destroy(instance, allocator);
}

VkResult VKAPI_CALL CreateWin32SurfaceKHR(VkInstance instance, const VkWin32SurfaceCreateInfoKHR* info, const VkAllocationCallbacks* allocator,
                                          VkSurfaceKHR* surface)
{
    auto* data = InstanceOf(Key(instance));
    VkResult result = data->CreateWin32SurfaceKHR(instance, info, allocator, surface);
    if (result == VK_SUCCESS)
    {
        std::lock_guard guard(g_lock);
        data->windows[*surface] = info->hwnd;
    }
    return result;
}

void VKAPI_CALL DestroySurfaceKHR(VkInstance instance, VkSurfaceKHR surface, const VkAllocationCallbacks* allocator)
{
    auto* data = InstanceOf(Key(instance));
    {
        std::lock_guard guard(g_lock);
        data->windows.erase(surface);
    }
    data->DestroySurfaceKHR(instance, surface, allocator);
}

// ---- Device ----

VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info, const VkAllocationCallbacks* allocator, VkDevice* out)
{
    auto* instance = InstanceOf(Key(physical));
    auto* link = (VkLayerDeviceCreateInfo*)info->pNext;
    while (link && !(link->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && link->function == VK_LAYER_LINK_INFO))
        link = (VkLayerDeviceCreateInfo*)link->pNext;
    auto* callback = (VkLayerDeviceCreateInfo*)info->pNext;
    while (callback && !(callback->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && callback->function == VK_LOADER_DATA_CALLBACK))
        callback = (VkLayerDeviceCreateInfo*)callback->pNext;
    if (!link || !instance) return VK_ERROR_INITIALIZATION_FAILED;
    auto nextInstance = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    auto nextDevice = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    link->u.pLayerInfo = link->u.pLayerInfo->pNext;
    auto create = (PFN_vkCreateDevice)nextInstance(instance->handle, "vkCreateDevice");
    VkResult result = create(physical, info, allocator, out);
    if (result != VK_SUCCESS) return result;

    auto data = std::make_unique<Device>();
    data->handle = *out;
    data->physical = physical;
    data->instance = instance;
    data->next = nextDevice;
    data->setLoaderData = callback ? callback->u.pfnSetDeviceLoaderData : nullptr;
#define F(name) data->name = (PFN_vk##name)nextDevice(*out, "vk" #name);
    F(DestroyDevice) F(GetDeviceQueue) F(GetDeviceQueue2) F(CreateSwapchainKHR) F(DestroySwapchainKHR) F(GetSwapchainImagesKHR)
    F(QueuePresentKHR) F(QueueSubmit) F(DeviceWaitIdle) F(CreateImageView) F(DestroyImageView) F(CreateFramebuffer)
    F(DestroyFramebuffer) F(CreateRenderPass) F(DestroyRenderPass) F(CreateCommandPool) F(DestroyCommandPool)
    F(AllocateCommandBuffers) F(ResetCommandBuffer) F(BeginCommandBuffer) F(EndCommandBuffer) F(CmdBeginRenderPass)
    F(CmdEndRenderPass) F(CmdPipelineBarrier) F(CmdCopyImageToBuffer) F(CreateFence) F(DestroyFence) F(WaitForFences)
    F(ResetFences) F(CreateSemaphore) F(DestroySemaphore) F(CreateBuffer) F(DestroyBuffer) F(GetBufferMemoryRequirements)
    F(AllocateMemory) F(FreeMemory) F(BindBufferMemory) F(MapMemory) F(UnmapMemory)
#undef F
    uint32_t count = 0;
    instance->GetPhysicalDeviceQueueFamilyProperties(physical, &count, nullptr);
    data->families.resize(count);
    instance->GetPhysicalDeviceQueueFamilyProperties(physical, &count, data->families.data());
    std::lock_guard guard(g_lock);
    g_physical[physical] = instance;
    g_devices[Key(*out)] = std::move(data);
    return VK_SUCCESS;
}

void DestroySwapchainData(Swapchain& s);

void VKAPI_CALL DestroyDevice(VkDevice device, const VkAllocationCallbacks* allocator)
{
    auto* data = DeviceOf(Key(device));
    if (!data) return;
    data->DeviceWaitIdle(device);
    {
        std::lock_guard guard(g_lock);
        for (auto it = g_swapchains.begin(); it != g_swapchains.end();)
        {
            if (it->second->device != data) { ++it; continue; }
            DestroySwapchainData(*it->second);
            it = g_swapchains.erase(it);
        }
    }
    if (data->imgui)
    {
        ImGui_ImplVulkan_Shutdown();
        overlay::Detach();
    }
    auto destroy = data->DestroyDevice;
    {
        std::lock_guard guard(g_lock);
        g_devices.erase(Key(device));
    }
    destroy(device, allocator);
}

void VKAPI_CALL GetDeviceQueue(VkDevice device, uint32_t family, uint32_t index, VkQueue* queue)
{
    auto* data = DeviceOf(Key(device));
    data->GetDeviceQueue(device, family, index, queue);
    std::lock_guard guard(g_lock);
    data->queueFamily[*queue] = family;
}

void VKAPI_CALL GetDeviceQueue2(VkDevice device, const VkDeviceQueueInfo2* info, VkQueue* queue)
{
    auto* data = DeviceOf(Key(device));
    data->GetDeviceQueue2(device, info, queue);
    std::lock_guard guard(g_lock);
    data->queueFamily[*queue] = info->queueFamilyIndex;
}

// ---- Swap chains ----

void DestroySwapchainData(Swapchain& s)
{
    auto* d = s.device;
    for (auto f : s.fences) d->DestroyFence(d->handle, f, nullptr);
    for (auto x : s.drawn) d->DestroySemaphore(d->handle, x, nullptr);
    if (s.pool) d->DestroyCommandPool(d->handle, s.pool, nullptr);
    for (auto f : s.framebuffers) d->DestroyFramebuffer(d->handle, f, nullptr);
    for (auto v : s.views) d->DestroyImageView(d->handle, v, nullptr);
    if (s.pass && s.pass != d->imguiPass) d->DestroyRenderPass(d->handle, s.pass, nullptr);
}

VkRenderPass MakePass(Device* d, VkFormat format)
{
    VkAttachmentDescription color{};
    color.format = format;
    color.samples = VK_SAMPLE_COUNT_1_BIT;
    color.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD; // over the game's frame
    color.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    color.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    color.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    color.initialLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    color.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    VkAttachmentReference ref{ 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
    VkSubpassDescription subpass{};
    subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    subpass.colorAttachmentCount = 1;
    subpass.pColorAttachments = &ref;
    VkSubpassDependency dependency{};
    dependency.srcSubpass = VK_SUBPASS_EXTERNAL;
    dependency.dstSubpass = 0;
    dependency.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dependency.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dependency.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    VkRenderPassCreateInfo info{ VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO };
    info.attachmentCount = 1;
    info.pAttachments = &color;
    info.subpassCount = 1;
    info.pSubpasses = &subpass;
    info.dependencyCount = 1;
    info.pDependencies = &dependency;
    VkRenderPass pass = VK_NULL_HANDLE;
    d->CreateRenderPass(d->handle, &info, nullptr, &pass);
    return pass;
}

VkResult VKAPI_CALL CreateSwapchainKHR(VkDevice device, const VkSwapchainCreateInfoKHR* info, const VkAllocationCallbacks* allocator,
                                       VkSwapchainKHR* out)
{
    auto* d = DeviceOf(Key(device));
    // Drawn into as a colour attachment; copied out only when a test asks for a frame.
    VkSwapchainCreateInfoKHR mine = *info;
    mine.imageUsage |= VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    bool dump = overlay::DumpPath() != nullptr;
    if (dump) mine.imageUsage |= VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    VkResult result = d->CreateSwapchainKHR(device, &mine, allocator, out);
    if (result != VK_SUCCESS && mine.imageUsage != info->imageUsage)
        result = d->CreateSwapchainKHR(device, info, allocator, out), dump = false;
    if (result != VK_SUCCESS) return result;

    auto s = std::make_unique<Swapchain>();
    s->device = d;
    s->handle = *out;
    s->format = info->imageFormat;
    s->extent = info->imageExtent;
    s->minImages = info->minImageCount < 2 ? 2 : info->minImageCount;
    s->canCopy = dump;
    {
        std::lock_guard guard(g_lock);
        auto w = d->instance->windows.find(info->surface);
        s->window = w == d->instance->windows.end() ? nullptr : w->second;
    }
    uint32_t count = 0;
    d->GetSwapchainImagesKHR(device, *out, &count, nullptr);
    s->images.resize(count);
    d->GetSwapchainImagesKHR(device, *out, &count, s->images.data());
    s->pass = d->imgui && d->imguiFormat == s->format ? d->imguiPass : MakePass(d, s->format);
    for (auto image : s->images)
    {
        VkImageViewCreateInfo view{ VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        view.image = image;
        view.viewType = VK_IMAGE_VIEW_TYPE_2D;
        view.format = s->format;
        view.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
        VkImageView v = VK_NULL_HANDLE;
        d->CreateImageView(device, &view, nullptr, &v);
        s->views.push_back(v);
        VkFramebufferCreateInfo fb{ VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO };
        fb.renderPass = s->pass;
        fb.attachmentCount = 1;
        fb.pAttachments = &v;
        fb.width = s->extent.width;
        fb.height = s->extent.height;
        fb.layers = 1;
        VkFramebuffer f = VK_NULL_HANDLE;
        d->CreateFramebuffer(device, &fb, nullptr, &f);
        s->framebuffers.push_back(f);
    }
    overlay::Log("swap chain %ux%u, format %d, %u images, window %p", s->extent.width, s->extent.height, (int)s->format, count, s->window);
    std::lock_guard guard(g_lock);
    g_swapchains[*out] = std::move(s);
    return VK_SUCCESS;
}

void VKAPI_CALL DestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain, const VkAllocationCallbacks* allocator)
{
    auto* d = DeviceOf(Key(device));
    std::unique_ptr<Swapchain> s;
    {
        std::lock_guard guard(g_lock);
        auto it = g_swapchains.find(swapchain);
        if (it != g_swapchains.end())
        {
            s = std::move(it->second);
            g_swapchains.erase(it);
        }
    }
    if (s)
    {
        if (!s->fences.empty()) d->WaitForFences(device, (uint32_t)s->fences.size(), s->fences.data(), VK_TRUE, UINT64_MAX);
        DestroySwapchainData(*s);
    }
    d->DestroySwapchainKHR(device, swapchain, allocator);
}

// ---- Drawing ----

/// ImGui's renderer loads its Vulkan functions through this: the next layer's, so its calls don't come back through
/// ours. Command buffers it allocates need the loader's dispatch pointer, like ours do.
Device* g_loading = nullptr;
PFN_vkAllocateCommandBuffers g_nextAllocate = nullptr;

VkResult VKAPI_CALL AllocateForImGui(VkDevice device, const VkCommandBufferAllocateInfo* info, VkCommandBuffer* out)
{
    VkResult result = g_nextAllocate(device, info, out);
    if (result == VK_SUCCESS && g_loading && g_loading->setLoaderData)
        for (uint32_t i = 0; i < info->commandBufferCount; i++) g_loading->setLoaderData(device, out[i]);
    return result;
}

PFN_vkVoidFunction LoadForImGui(const char* name, void*)
{
    auto* d = g_loading;
    if (strcmp(name, "vkAllocateCommandBuffers") == 0)
    {
        g_nextAllocate = d->AllocateCommandBuffers;
        return (PFN_vkVoidFunction)AllocateForImGui;
    }
    if (auto f = d->next(d->handle, name)) return f;
    return d->instance->next(d->instance->handle, name);
}

bool SetUpImGui(Device* d, VkQueue queue, uint32_t family, Swapchain* s)
{
    if (d->imgui)
    {
        if (d->imguiFormat != s->format)
        {
            // A new swap chain in another format: ImGui's pipeline needs a render pass for it.
            ImGui_ImplVulkan_PipelineInfo pipeline{};
            pipeline.RenderPass = s->pass;
            ImGui_ImplVulkan_CreateMainPipeline(&pipeline);
            d->imguiPass = s->pass;
            d->imguiFormat = s->format;
        }
        return true;
    }
    g_loading = d;
    if (!ImGui_ImplVulkan_LoadFunctions(d->instance->api, LoadForImGui))
    {
        overlay::Problem("Couldn't load Vulkan functions for drawing");
        return false;
    }
    ImGui_ImplVulkan_InitInfo info{};
    info.ApiVersion = d->instance->api;
    info.Instance = d->instance->handle;
    info.PhysicalDevice = d->physical;
    info.Device = d->handle;
    info.QueueFamily = family;
    info.Queue = queue;
    info.DescriptorPoolSize = 64;
    info.MinImageCount = s->minImages;
    info.ImageCount = (uint32_t)s->images.size() < s->minImages ? s->minImages : (uint32_t)s->images.size();
    info.PipelineInfoMain.RenderPass = s->pass;
    if (!ImGui_ImplVulkan_Init(&info))
    {
        overlay::Problem("Couldn't set up drawing on this Vulkan device");
        return false;
    }
    d->imgui = true;
    d->imguiPass = s->pass;
    d->imguiFormat = s->format;
    return true;
}

bool PrepareCommands(Device* d, Swapchain* s, uint32_t family)
{
    if (s->family == family && s->pool) return true;
    if (s->pool) return false; // drew from another family before; don't mix (rare)
    VkCommandPoolCreateInfo pool{ VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    pool.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pool.queueFamilyIndex = family;
    if (d->CreateCommandPool(d->handle, &pool, nullptr, &s->pool) != VK_SUCCESS) return false;
    s->family = family;
    auto n = (uint32_t)s->images.size();
    s->commands.resize(n);
    VkCommandBufferAllocateInfo alloc{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    alloc.commandPool = s->pool;
    alloc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    alloc.commandBufferCount = n;
    if (d->AllocateCommandBuffers(d->handle, &alloc, s->commands.data()) != VK_SUCCESS) return false;
    for (auto c : s->commands)
        if (d->setLoaderData) d->setLoaderData(d->handle, c);
    for (uint32_t i = 0; i < n; i++)
    {
        VkFenceCreateInfo fence{ VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        fence.flags = VK_FENCE_CREATE_SIGNALED_BIT;
        VkFence f;
        d->CreateFence(d->handle, &fence, nullptr, &f);
        s->fences.push_back(f);
        VkSemaphoreCreateInfo sem{ VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
        VkSemaphore x;
        d->CreateSemaphore(d->handle, &sem, nullptr, &x);
        s->drawn.push_back(x);
    }
    return true;
}

uint32_t MemoryType(Device* d, uint32_t bits, VkMemoryPropertyFlags wanted)
{
    VkPhysicalDeviceMemoryProperties props;
    d->instance->GetPhysicalDeviceMemoryProperties(d->physical, &props);
    for (uint32_t i = 0; i < props.memoryTypeCount; i++)
        if ((bits & (1u << i)) && (props.memoryTypes[i].propertyFlags & wanted) == wanted) return i;
    return UINT32_MAX;
}

/// Test only (DRANGED_OVERLAY_DUMP): copies the presented image out and writes it as a BMP.
struct Dump
{
    VkBuffer buffer = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
};

bool StartDump(Device* d, Swapchain* s, VkCommandBuffer cmd, VkImage image, Dump& dump)
{
    VkBufferCreateInfo b{ VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
    b.size = (VkDeviceSize)s->extent.width * s->extent.height * 4;
    b.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    if (d->CreateBuffer(d->handle, &b, nullptr, &dump.buffer) != VK_SUCCESS) return false;
    VkMemoryRequirements req;
    d->GetBufferMemoryRequirements(d->handle, dump.buffer, &req);
    VkMemoryAllocateInfo a{ VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    a.allocationSize = req.size;
    a.memoryTypeIndex = MemoryType(d, req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (a.memoryTypeIndex == UINT32_MAX || d->AllocateMemory(d->handle, &a, nullptr, &dump.memory) != VK_SUCCESS) return false;
    d->BindBufferMemory(d->handle, dump.buffer, dump.memory, 0);
    VkImageMemoryBarrier barrier{ VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
    barrier.srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    barrier.oldLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    barrier.srcQueueFamilyIndex = barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    barrier.image = image;
    barrier.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
    d->CmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
    VkBufferImageCopy copy{};
    copy.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
    copy.imageExtent = { s->extent.width, s->extent.height, 1 };
    d->CmdCopyImageToBuffer(cmd, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, dump.buffer, 1, &copy);
    barrier.srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    barrier.dstAccessMask = 0;
    barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    barrier.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    d->CmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
    return true;
}

void FinishDump(Device* d, Swapchain* s, VkFence fence, Dump& dump)
{
    d->WaitForFences(d->handle, 1, &fence, VK_TRUE, UINT64_MAX);
    void* pixels = nullptr;
    if (d->MapMemory(d->handle, dump.memory, 0, VK_WHOLE_SIZE, 0, &pixels) == VK_SUCCESS)
    {
        uint32_t w = s->extent.width, h = s->extent.height;
        bool rgba = s->format == VK_FORMAT_R8G8B8A8_UNORM || s->format == VK_FORMAT_R8G8B8A8_SRGB;
        std::vector<uint8_t> bgra((size_t)w * h * 4);
        memcpy(bgra.data(), pixels, bgra.size());
        if (rgba)
            for (size_t i = 0; i < bgra.size(); i += 4) std::swap(bgra[i], bgra[i + 2]);
        BITMAPFILEHEADER file{};
        BITMAPINFOHEADER head{ sizeof(BITMAPINFOHEADER), (LONG)w, -(LONG)h, 1, 32, BI_RGB };
        file.bfType = 0x4D42;
        file.bfOffBits = sizeof file + sizeof head;
        file.bfSize = file.bfOffBits + (DWORD)bgra.size();
        if (FILE* f = fopen(overlay::DumpPath(), "wb"))
        {
            fwrite(&file, sizeof file, 1, f);
            fwrite(&head, sizeof head, 1, f);
            fwrite(bgra.data(), 1, bgra.size(), f);
            fclose(f);
            overlay::Log("wrote a frame to %s", overlay::DumpPath());
        }
        d->UnmapMemory(d->handle, dump.memory);
    }
    d->DestroyBuffer(d->handle, dump.buffer, nullptr);
    d->FreeMemory(d->handle, dump.memory, nullptr);
}

VkResult VKAPI_CALL QueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* info)
{
    auto* d = DeviceOf(Key(queue));
    if (!d || info->swapchainCount == 0) return d ? d->QueuePresentKHR(queue, info) : VK_ERROR_DEVICE_LOST;
    Swapchain* s = nullptr;
    uint32_t family = UINT32_MAX;
    {
        std::lock_guard guard(g_lock);
        auto it = g_swapchains.find(info->pSwapchains[0]);
        if (it != g_swapchains.end()) s = it->second.get();
        auto q = d->queueFamily.find(queue);
        if (q != d->queueFamily.end()) family = q->second;
    }
    if (!s || family == UINT32_MAX || !(d->families[family].queueFlags & VK_QUEUE_GRAPHICS_BIT))
        return d->QueuePresentKHR(queue, info);

    overlay::Attach(s->window);
    uint32_t index = info->pImageIndices[0];
    VkSemaphore drawn = VK_NULL_HANDLE;
    bool ok = true;
    Dump dump;
    bool dumping = false;
    overlay::Frame(
        [&] {
            ok = SetUpImGui(d, queue, family, s) && PrepareCommands(d, s, family);
            if (ok) ImGui_ImplVulkan_NewFrame();
        },
        [&] {
            if (!ok) return;
            VkFence fence = s->fences[index];
            VkCommandBuffer cmd = s->commands[index];
            d->WaitForFences(d->handle, 1, &fence, VK_TRUE, UINT64_MAX);
            d->ResetFences(d->handle, 1, &fence);
            d->ResetCommandBuffer(cmd, 0);
            VkCommandBufferBeginInfo begin{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
            begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
            d->BeginCommandBuffer(cmd, &begin);
            VkRenderPassBeginInfo pass{ VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO };
            pass.renderPass = s->pass;
            pass.framebuffer = s->framebuffers[index];
            pass.renderArea.extent = s->extent;
            d->CmdBeginRenderPass(cmd, &pass, VK_SUBPASS_CONTENTS_INLINE);
            if (IsSrgb(s->format)) LinearizeColours(ImGui::GetDrawData());
            ImGui_ImplVulkan_RenderDrawData(ImGui::GetDrawData(), cmd);
            d->CmdEndRenderPass(cmd);
            if (s->canCopy && !g_dumped && overlay::DumpPath() && ++g_drawnFrames >= 5) dumping = StartDump(d, s, cmd, s->images[index], dump);
            d->EndCommandBuffer(cmd);

            std::vector<VkPipelineStageFlags> stages(info->waitSemaphoreCount, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
            VkSubmitInfo submit{ VK_STRUCTURE_TYPE_SUBMIT_INFO };
            submit.waitSemaphoreCount = info->waitSemaphoreCount;
            submit.pWaitSemaphores = info->pWaitSemaphores;
            submit.pWaitDstStageMask = stages.data();
            submit.commandBufferCount = 1;
            submit.pCommandBuffers = &cmd;
            submit.signalSemaphoreCount = 1;
            submit.pSignalSemaphores = &s->drawn[index];
            if (d->QueueSubmit(queue, 1, &submit, fence) == VK_SUCCESS) drawn = s->drawn[index];
            else
            {
                // Nothing will signal the fence now: replace it with a signalled one so the next frame doesn't wait forever.
                d->DestroyFence(d->handle, fence, nullptr);
                VkFenceCreateInfo fresh{ VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
                fresh.flags = VK_FENCE_CREATE_SIGNALED_BIT;
                d->CreateFence(d->handle, &fresh, nullptr, &s->fences[index]);
                dumping = false;
            }
            if (dumping)
            {
                FinishDump(d, s, fence, dump);
                g_dumped = true;
            }
        });
    if (!drawn) return d->QueuePresentKHR(queue, info);
    VkPresentInfoKHR mine = *info;
    mine.waitSemaphoreCount = 1;
    mine.pWaitSemaphores = &drawn;
    return d->QueuePresentKHR(queue, &mine);
}

// ---- Entry points ----

PFN_vkVoidFunction VKAPI_CALL GetDeviceProcAddr(VkDevice device, const char* name);

PFN_vkVoidFunction Ours(const char* name)
{
#define H(fn) if (strcmp(name, "vk" #fn) == 0) return (PFN_vkVoidFunction)fn;
    H(CreateInstance) H(DestroyInstance) H(CreateWin32SurfaceKHR) H(DestroySurfaceKHR) H(CreateDevice) H(DestroyDevice)
    H(GetDeviceQueue) H(GetDeviceQueue2) H(CreateSwapchainKHR) H(DestroySwapchainKHR) H(QueuePresentKHR) H(GetDeviceProcAddr)
#undef H
    return nullptr;
}

PFN_vkVoidFunction VKAPI_CALL GetInstanceProcAddr(VkInstance instance, const char* name)
{
    if (strcmp(name, "vkGetInstanceProcAddr") == 0) return (PFN_vkVoidFunction)GetInstanceProcAddr;
    if (auto f = Ours(name)) return f;
    if (!instance) return nullptr;
    auto* data = InstanceOf(Key(instance));
    return data ? data->next(instance, name) : nullptr;
}

PFN_vkVoidFunction VKAPI_CALL GetDeviceProcAddr(VkDevice device, const char* name)
{
    if (strcmp(name, "vkCreateInstance") != 0 && strcmp(name, "vkCreateDevice") != 0 && strcmp(name, "vkDestroyInstance") != 0)
        if (auto f = Ours(name)) return f;
    auto* data = DeviceOf(Key(device));
    return data ? data->next(device, name) : nullptr;
}

} // namespace

// Declared in vk_layer.h; exported by name for the manifest.
#pragma comment(linker, "/EXPORT:vkNegotiateLoaderLayerInterfaceVersion")
extern "C" VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface* p)
{
    if (!p || p->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT) return VK_ERROR_INITIALIZATION_FAILED;
    if (p->loaderLayerInterfaceVersion > 2) p->loaderLayerInterfaceVersion = 2;
    p->pfnGetInstanceProcAddr = GetInstanceProcAddr;
    p->pfnGetDeviceProcAddr = GetDeviceProcAddr;
    p->pfnGetPhysicalDeviceProcAddr = nullptr;
    return VK_SUCCESS;
}
