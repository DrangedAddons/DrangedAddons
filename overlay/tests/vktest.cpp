// A minimal Vulkan program for testing the overlay layer without a game: a window that's never shown, a swap chain on
// it, and a few frames cleared to one colour and presented. Run with the layer enabled (see tests\README.md) and
// DRANGED_OVERLAY_DUMP set to get the last frame, overlay included, as a BMP.
#include <windows.h>
#include <vulkan/vulkan.h>
#undef CreateSemaphore // windows.h's macro, not Vulkan's function
#include <cstdio>
#include <cstring>
#include <csignal>
#include <exception>
#include <dbghelp.h>
#pragma comment(lib, "dbghelp.lib")

// When the program aborts or an exception escapes, print where (with the overlay DLL's symbols when its PDB is there).
void PrintStack(const char* why)
{
    printf("=== %s\n", why);
    HANDLE process = GetCurrentProcess();
    SymSetOptions(SYMOPT_UNDNAME | SYMOPT_LOAD_LINES | SYMOPT_DEFERRED_LOADS);
    char exe[MAX_PATH];
    GetModuleFileNameA(nullptr, exe, MAX_PATH);
    *strrchr(exe, 92) = 0; // symbols next to the programs, in bin
    SymInitialize(process, exe, TRUE);
    void* frames[64];
    USHORT n = CaptureStackBackTrace(0, 64, frames, nullptr);
    for (USHORT i = 0; i < n; i++)
    {
        char buffer[sizeof(SYMBOL_INFO) + 256] = {};
        auto* symbol = (SYMBOL_INFO*)buffer;
        symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
        symbol->MaxNameLen = 255;
        DWORD64 offset = 0;
        IMAGEHLP_LINE64 line{ sizeof line };
        DWORD lineOffset = 0;
        char module[MAX_PATH] = "?";
        HMODULE m = nullptr;
        if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)frames[i], &m))
            GetModuleFileNameA(m, module, MAX_PATH);
        const char* base = strrchr(module, 92);
        bool named = SymFromAddr(process, (DWORD64)frames[i], &offset, symbol);
        bool lined = SymGetLineFromAddr64(process, (DWORD64)frames[i], &lineOffset, &line);
        printf("  %-22s %s%s", base ? base + 1 : module, named ? symbol->Name : "?", lined ? "" : "\n");
        if (lined) printf("  (%s:%lu)\n", strrchr(line.FileName, 92) ? strrchr(line.FileName, 92) + 1 : line.FileName, line.LineNumber);
    }
    fflush(stdout);
}
#include <vector>

#define FAIL(what) do { printf("FAILED: %s\n", what); return 1; } while (0)
#define CHECK(call) do { VkResult r_ = (call); if (r_ != VK_SUCCESS) { printf("FAILED: %s = %d\n", #call, (int)r_); return 1; } } while (0)

PFN_vkGetInstanceProcAddr gipa;
#define I(name) PFN_vk##name name;
I(CreateInstance) I(EnumeratePhysicalDevices) I(GetPhysicalDeviceQueueFamilyProperties) I(GetPhysicalDeviceSurfaceSupportKHR)
I(GetPhysicalDeviceSurfaceFormatsKHR) I(GetPhysicalDeviceSurfaceCapabilitiesKHR) I(CreateWin32SurfaceKHR) I(CreateDevice)
I(GetDeviceProcAddr) I(DestroyInstance) I(DestroySurfaceKHR)
I(GetDeviceQueue) I(CreateSwapchainKHR) I(GetSwapchainImagesKHR) I(AcquireNextImageKHR) I(QueuePresentKHR) I(CreateImageView)
I(CreateRenderPass) I(CreateFramebuffer) I(CreateCommandPool) I(AllocateCommandBuffers) I(BeginCommandBuffer) I(EndCommandBuffer)
I(CmdBeginRenderPass) I(CmdEndRenderPass) I(QueueSubmit) I(QueueWaitIdle) I(CreateSemaphore) I(DeviceWaitIdle) I(DestroySwapchainKHR)
I(DestroyDevice) I(ResetCommandBuffer)
#undef I

int main(int argc, char** argv)
{
    // First chance: a C++ exception being thrown anywhere (E06D7363), with its type, before anything handles it.
    AddVectoredExceptionHandler(1, [](EXCEPTION_POINTERS* e) -> LONG {
        auto* r = e->ExceptionRecord;
        if (r->ExceptionCode != 0xE06D7363 || r->NumberParameters < 4) return EXCEPTION_CONTINUE_SEARCH;
        auto base = (char*)r->ExceptionInformation[3];
        auto throwInfo = base + *(int*)((char*)r->ExceptionInformation[2] + 12);  // ThrowInfo.pCatchableTypeArray
        auto first = base + *(int*)(throwInfo + 4);                               // first CatchableType
        auto type = (const char*)(base + *(int*)(first + 4)) + 16;                // its TypeDescriptor's name
        char why[256];
        snprintf(why, sizeof why, "C++ exception thrown: %s", type);
        PrintStack(why);
        return EXCEPTION_CONTINUE_SEARCH;
    });
    signal(SIGABRT, [](int) { PrintStack("abort"); });
    std::set_terminate([] { PrintStack("terminate"); abort(); });
    SetUnhandledExceptionFilter([](EXCEPTION_POINTERS* e) -> LONG {
        char why[64];
        snprintf(why, sizeof why, "unhandled exception %08lX", e->ExceptionRecord->ExceptionCode);
        PrintStack(why);
        return EXCEPTION_CONTINUE_SEARCH;
    });
    setvbuf(stdout, nullptr, _IONBF, 0);
    int frames = argc > 1 ? atoi(argv[1]) : 30;
    // "click": from frame 10, posts clicks and typing to the (hidden) window, as a player using the overlay would.
    bool click = argc > 2 && strcmp(argv[2], "click") == 0;
    HMODULE vulkan = LoadLibraryW(L"vulkan-1.dll");
    if (!vulkan) FAIL("no vulkan-1.dll");
    gipa = (PFN_vkGetInstanceProcAddr)GetProcAddress(vulkan, "vkGetInstanceProcAddr");
    CreateInstance = (PFN_vkCreateInstance)gipa(nullptr, "vkCreateInstance");

    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"DrangedOverlayTest";
    RegisterClassW(&wc);
    // Never shown: nothing appears on screen.
    // Third argument: the client size, e.g. 1920x1080 (default 1280x720).
    int width = 1280, height = 720;
    if (argc > 3) sscanf(argv[3], "%dx%d", &width, &height);
    RECT frame{ 0, 0, width, height };
    AdjustWindowRect(&frame, WS_POPUP, FALSE);
    HWND window = CreateWindowExW(0, wc.lpszClassName, L"overlay test", WS_POPUP, 0, 0, frame.right - frame.left, frame.bottom - frame.top,
                                  nullptr, nullptr, wc.hInstance, nullptr);
    RECT client;
    GetClientRect(window, &client);

    const char* extensions[] = { "VK_KHR_surface", "VK_KHR_win32_surface" };
    VkApplicationInfo app{ VK_STRUCTURE_TYPE_APPLICATION_INFO };
    app.apiVersion = VK_API_VERSION_1_2;
    VkInstanceCreateInfo ici{ VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
    ici.pApplicationInfo = &app;
    ici.enabledExtensionCount = 2;
    ici.ppEnabledExtensionNames = extensions;
    VkInstance instance;
    CHECK(CreateInstance(&ici, nullptr, &instance));
#define L(name) name = (PFN_vk##name)gipa(instance, "vk" #name);
    L(EnumeratePhysicalDevices) L(GetPhysicalDeviceQueueFamilyProperties) L(GetPhysicalDeviceSurfaceSupportKHR)
    L(GetPhysicalDeviceSurfaceFormatsKHR) L(GetPhysicalDeviceSurfaceCapabilitiesKHR) L(CreateWin32SurfaceKHR) L(CreateDevice)
    L(GetDeviceProcAddr) L(DestroyInstance) L(DestroySurfaceKHR)
#undef L

    VkWin32SurfaceCreateInfoKHR sci{ VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR };
    sci.hinstance = wc.hInstance;
    sci.hwnd = window;
    VkSurfaceKHR surface;
    CHECK(CreateWin32SurfaceKHR(instance, &sci, nullptr, &surface));

    uint32_t count = 0;
    EnumeratePhysicalDevices(instance, &count, nullptr);
    std::vector<VkPhysicalDevice> gpus(count);
    EnumeratePhysicalDevices(instance, &count, gpus.data());
    VkPhysicalDevice gpu = VK_NULL_HANDLE;
    uint32_t family = UINT32_MAX;
    for (auto g : gpus)
    {
        GetPhysicalDeviceQueueFamilyProperties(g, &count, nullptr);
        std::vector<VkQueueFamilyProperties> families(count);
        GetPhysicalDeviceQueueFamilyProperties(g, &count, families.data());
        for (uint32_t i = 0; i < count && family == UINT32_MAX; i++)
        {
            VkBool32 present = VK_FALSE;
            GetPhysicalDeviceSurfaceSupportKHR(g, i, surface, &present);
            if ((families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) && present) { gpu = g; family = i; }
        }
        if (gpu) break;
    }
    if (!gpu) FAIL("no GPU that can present");

    float priority = 1;
    VkDeviceQueueCreateInfo qci{ VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
    qci.queueFamilyIndex = family;
    qci.queueCount = 1;
    qci.pQueuePriorities = &priority;
    const char* deviceExtensions[] = { "VK_KHR_swapchain" };
    VkDeviceCreateInfo dci{ VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
    dci.queueCreateInfoCount = 1;
    dci.pQueueCreateInfos = &qci;
    dci.enabledExtensionCount = 1;
    dci.ppEnabledExtensionNames = deviceExtensions;
    VkDevice device;
    CHECK(CreateDevice(gpu, &dci, nullptr, &device));
#define D(name) name = (PFN_vk##name)GetDeviceProcAddr(device, "vk" #name);
    D(GetDeviceQueue) D(CreateSwapchainKHR) D(GetSwapchainImagesKHR) D(AcquireNextImageKHR) D(QueuePresentKHR) D(CreateImageView)
    D(CreateRenderPass) D(CreateFramebuffer) D(CreateCommandPool) D(AllocateCommandBuffers) D(BeginCommandBuffer) D(EndCommandBuffer)
    D(CmdBeginRenderPass) D(CmdEndRenderPass) D(QueueSubmit) D(QueueWaitIdle) D(CreateSemaphore) D(DeviceWaitIdle) D(DestroySwapchainKHR)
    D(DestroyDevice) D(ResetCommandBuffer)
#undef D
    VkQueue queue;
    GetDeviceQueue(device, family, 0, &queue);

    GetPhysicalDeviceSurfaceFormatsKHR(gpu, surface, &count, nullptr);
    std::vector<VkSurfaceFormatKHR> formats(count);
    GetPhysicalDeviceSurfaceFormatsKHR(gpu, surface, &count, formats.data());
    VkSurfaceFormatKHR format = formats[0];
    for (auto f : formats)
        if (f.format == VK_FORMAT_B8G8R8A8_SRGB) format = f; // what wgpu picks
    VkSurfaceCapabilitiesKHR caps;
    GetPhysicalDeviceSurfaceCapabilitiesKHR(gpu, surface, &caps);
    VkExtent2D extent = caps.currentExtent.width != UINT32_MAX ? caps.currentExtent : VkExtent2D{ (uint32_t)client.right, (uint32_t)client.bottom };
    printf("swap chain %ux%u format %d\n", extent.width, extent.height, (int)format.format);

    VkSwapchainCreateInfoKHR swci{ VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR };
    swci.surface = surface;
    swci.minImageCount = caps.minImageCount < 2 ? 2 : caps.minImageCount;
    swci.imageFormat = format.format;
    swci.imageColorSpace = format.colorSpace;
    swci.imageExtent = extent;
    swci.imageArrayLayers = 1;
    swci.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    swci.preTransform = caps.currentTransform;
    swci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    swci.presentMode = VK_PRESENT_MODE_FIFO_KHR;
    swci.clipped = VK_TRUE;
    VkSwapchainKHR swapchain;
    CHECK(CreateSwapchainKHR(device, &swci, nullptr, &swapchain));
    GetSwapchainImagesKHR(device, swapchain, &count, nullptr);
    std::vector<VkImage> images(count);
    GetSwapchainImagesKHR(device, swapchain, &count, images.data());

    VkAttachmentDescription color{};
    color.format = format.format;
    color.samples = VK_SAMPLE_COUNT_1_BIT;
    color.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    color.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    color.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    color.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    VkAttachmentReference ref{ 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
    VkSubpassDescription sub{};
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &ref;
    VkRenderPassCreateInfo rpci{ VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO };
    rpci.attachmentCount = 1;
    rpci.pAttachments = &color;
    rpci.subpassCount = 1;
    rpci.pSubpasses = &sub;
    VkRenderPass pass;
    CHECK(CreateRenderPass(device, &rpci, nullptr, &pass));
    std::vector<VkFramebuffer> framebuffers;
    for (auto image : images)
    {
        VkImageViewCreateInfo v{ VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        v.image = image;
        v.viewType = VK_IMAGE_VIEW_TYPE_2D;
        v.format = format.format;
        v.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
        VkImageView view;
        CHECK(CreateImageView(device, &v, nullptr, &view));
        VkFramebufferCreateInfo f{ VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO };
        f.renderPass = pass;
        f.attachmentCount = 1;
        f.pAttachments = &view;
        f.width = extent.width;
        f.height = extent.height;
        f.layers = 1;
        VkFramebuffer fb;
        CHECK(CreateFramebuffer(device, &f, nullptr, &fb));
        framebuffers.push_back(fb);
    }

    VkCommandPoolCreateInfo pci{ VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    pci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pci.queueFamilyIndex = family;
    VkCommandPool pool;
    CHECK(CreateCommandPool(device, &pci, nullptr, &pool));
    VkCommandBufferAllocateInfo cai{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    cai.commandPool = pool;
    cai.commandBufferCount = 1;
    VkCommandBuffer cmd;
    CHECK(AllocateCommandBuffers(device, &cai, &cmd));
    VkSemaphoreCreateInfo semi{ VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    VkSemaphore acquired, rendered;
    CreateSemaphore(device, &semi, nullptr, &acquired);
    CreateSemaphore(device, &semi, nullptr, &rendered);

    for (int frame = 0; frame < frames; frame++)
    {
        // VKTEST_CLICK=x,y[;x,y...]: one click at each, from frame 60, 30 frames apart (after the overlay has its state).
        static int clicks[8][2];
        static int clickCount = 0;
        if (frame == 0)
        {
            char at[128] = {};
            if (GetEnvironmentVariableA("VKTEST_CLICK", at, sizeof at))
                for (char* part = strtok(at, ";"); part && clickCount < 8; part = strtok(nullptr, ";"))
                    if (sscanf(part, "%d,%d", &clicks[clickCount][0], &clicks[clickCount][1]) == 2) clickCount++;
        }
        if (frame >= 60 && (frame - 60) % 30 == 0 && (frame - 60) / 30 < clickCount)
        {
            // Move, press and release together: the real cursor isn't over this hidden window, so Windows says the
            // mouse left as soon as ImGui asks, and a click spread over frames would lose its hover.
            auto* c = clicks[(frame - 60) / 30];
            LPARAM at = MAKELPARAM(c[0], c[1]);
            PostMessageW(window, WM_MOUSEMOVE, 0, at);
            PostMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON, at);
            PostMessageW(window, WM_LBUTTONUP, 0, at);
        }
        if (click && frame >= 10 && frame < 40)
        {
            int x = (int)extent.width / 2, y = (int)extent.height / 2 + (frame % 2 ? 40 : -20);
            LPARAM at = MAKELPARAM(x, y);
            PostMessageW(window, WM_MOUSEMOVE, 0, at);
            PostMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON, at);
            PostMessageW(window, WM_LBUTTONUP, 0, at);
            PostMessageW(window, WM_KEYDOWN, 'A', 1);
            PostMessageW(window, WM_CHAR, 'a', 1);
            PostMessageW(window, WM_KEYUP, 'A', 0xC0000001);
        }
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        uint32_t index;
        CHECK(AcquireNextImageKHR(device, swapchain, UINT64_MAX, acquired, VK_NULL_HANDLE, &index));
        ResetCommandBuffer(cmd, 0);
        VkCommandBufferBeginInfo begin{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
        BeginCommandBuffer(cmd, &begin);
        VkClearValue clear{};
        clear.color = { { 0.05f, 0.25f, 0.30f, 1 } }; // a dark teal "game"
        VkRenderPassBeginInfo rbi{ VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO };
        rbi.renderPass = pass;
        rbi.framebuffer = framebuffers[index];
        rbi.renderArea.extent = extent;
        rbi.clearValueCount = 1;
        rbi.pClearValues = &clear;
        CmdBeginRenderPass(cmd, &rbi, VK_SUBPASS_CONTENTS_INLINE);
        CmdEndRenderPass(cmd);
        EndCommandBuffer(cmd);
        VkPipelineStageFlags stage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        VkSubmitInfo si{ VK_STRUCTURE_TYPE_SUBMIT_INFO };
        si.waitSemaphoreCount = 1;
        si.pWaitSemaphores = &acquired;
        si.pWaitDstStageMask = &stage;
        si.commandBufferCount = 1;
        si.pCommandBuffers = &cmd;
        si.signalSemaphoreCount = 1;
        si.pSignalSemaphores = &rendered;
        CHECK(QueueSubmit(queue, 1, &si, VK_NULL_HANDLE));
        VkPresentInfoKHR pi{ VK_STRUCTURE_TYPE_PRESENT_INFO_KHR };
        pi.waitSemaphoreCount = 1;
        pi.pWaitSemaphores = &rendered;
        pi.swapchainCount = 1;
        pi.pSwapchains = &swapchain;
        pi.pImageIndices = &index;
        VkResult r = QueuePresentKHR(queue, &pi);
        if (r != VK_SUCCESS && r != VK_SUBOPTIMAL_KHR) { printf("present returned %d\n", (int)r); return 1; }
        QueueWaitIdle(queue);
        Sleep(16); // about 60 frames a second: a hidden window isn't held to the display's rate
    }
    DeviceWaitIdle(device);
    DestroySwapchainKHR(device, swapchain, nullptr);
    DestroyDevice(device, nullptr);
    DestroySurfaceKHR(instance, surface, nullptr);
    DestroyInstance(instance, nullptr);
    printf("presented %d frames\n", frames);
    return 0;
}
