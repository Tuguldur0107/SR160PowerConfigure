/*
 * Test different HID report formats for Chainway SR160.
 * The device might expect a different frame format over HID vs serial.
 *
 * Tries multiple approaches:
 * 1. Raw BB...7E frame at offset 0 (current)
 * 2. Length byte prefix: [len] [BB...7E]
 * 3. Frame at offset 1: [0x00] [BB...7E]
 * 4. Report ID 1 with SetReport
 * 5. Without seize (shared mode)
 */

#include <IOKit/hid/IOHIDManager.h>
#include <CoreFoundation/CoreFoundation.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <pthread.h>

#define SR160_VID       0x2047
#define SR160_PID       0x0301
#define CMD_USAGE_PAGE  0x8C
#define CMD_USAGE       0x01
#define HID_REPORT_LEN  64

static IOHIDManagerRef  g_manager = NULL;
static IOHIDDeviceRef   g_device  = NULL;
static CFRunLoopRef     g_runloop = NULL;
static pthread_t        g_thread;
static volatile int     g_callback_count = 0;

static void hex_dump(const char *label, const uint8_t *data, int len)
{
    printf("%s [%d]: ", label, len);
    for (int i = 0; i < len && i < 20; i++) printf("%02X ", data[i]);
    if (len > 20) printf("...");
    printf("\n");
}

static void input_cb(void *ctx, IOReturn result, void *sender,
                     IOHIDReportType type, uint32_t reportID,
                     uint8_t *report, CFIndex len)
{
    (void)ctx; (void)sender; (void)type;
    g_callback_count++;
    printf("*** CALLBACK #%d: result=0x%08X, id=%u, len=%ld\n",
           g_callback_count, (unsigned)result, (unsigned)reportID, (long)len);
    hex_dump("  DATA", report, (int)len);
}

static void matched_cb(void *ctx, IOReturn result, void *sender, IOHIDDeviceRef dev)
{
    (void)ctx; (void)result; (void)sender;
    if (g_device) return;

    printf("Device matched!\n");

    /* Try seize first */
    IOReturn rc = IOHIDDeviceOpen(dev, kIOHIDOptionsTypeSeizeDevice);
    printf("  Open(seize): 0x%08X\n", (unsigned)rc);
    if (rc != kIOReturnSuccess) {
        rc = IOHIDDeviceOpen(dev, kIOHIDOptionsTypeNone);
        printf("  Open(shared): 0x%08X\n", (unsigned)rc);
    }
    if (rc != kIOReturnSuccess) return;

    g_device = dev;

    IOHIDDeviceScheduleWithRunLoop(dev, CFRunLoopGetCurrent(), kCFRunLoopDefaultMode);

    static uint8_t buf[HID_REPORT_LEN];
    IOHIDDeviceRegisterInputReportCallback(dev, buf, sizeof(buf), input_cb, NULL);

    /* Print report sizes */
    int maxIn = 0, maxOut = 0;
    CFNumberRef nIn = IOHIDDeviceGetProperty(dev, CFSTR(kIOHIDMaxInputReportSizeKey));
    CFNumberRef nOut = IOHIDDeviceGetProperty(dev, CFSTR(kIOHIDMaxOutputReportSizeKey));
    if (nIn) CFNumberGetValue(nIn, kCFNumberIntType, &maxIn);
    if (nOut) CFNumberGetValue(nOut, kCFNumberIntType, &maxOut);
    printf("  maxInput=%d, maxOutput=%d\n", maxIn, maxOut);
}

static void *runloop_fn(void *arg)
{
    (void)arg;
    g_runloop = CFRunLoopGetCurrent();
    IOHIDManagerScheduleWithRunLoop(g_manager, g_runloop, kCFRunLoopDefaultMode);
    IOHIDManagerOpen(g_manager, kIOHIDOptionsTypeNone);
    CFRunLoopRun();
    return NULL;
}

static void send_and_wait(const char *desc, int reportID, const uint8_t *data, int len)
{
    printf("\n--- %s (reportID=%d) ---\n", desc, reportID);
    hex_dump("  TX", data, len);

    g_callback_count = 0;

    IOReturn ret = IOHIDDeviceSetReport(g_device,
        kIOHIDReportTypeOutput, reportID, data, len);
    printf("  SetReport(Output,id=%d): 0x%08X\n", reportID, (unsigned)ret);

    if (ret != kIOReturnSuccess) {
        ret = IOHIDDeviceSetReport(g_device,
            kIOHIDReportTypeFeature, reportID, data, len);
        printf("  SetReport(Feature,id=%d): 0x%08X\n", reportID, (unsigned)ret);
    }

    /* Wait 2 seconds for callback */
    for (int i = 0; i < 20; i++) {
        usleep(100000);
        if (g_callback_count > 0) {
            printf("  Got callback after %dms!\n", (i + 1) * 100);
            return;
        }
    }
    printf("  No callback after 2s\n");
}

int main(void)
{
    g_manager = IOHIDManagerCreate(kCFAllocatorDefault, kIOHIDOptionsTypeNone);

    CFMutableDictionaryRef match = CFDictionaryCreateMutable(
        kCFAllocatorDefault, 4,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    int vid = SR160_VID, pid = SR160_PID;
    int upage = CMD_USAGE_PAGE, usg = CMD_USAGE;
    CFNumberRef cvid = CFNumberCreate(NULL, kCFNumberIntType, &vid);
    CFNumberRef cpid = CFNumberCreate(NULL, kCFNumberIntType, &pid);
    CFNumberRef cpage = CFNumberCreate(NULL, kCFNumberIntType, &upage);
    CFNumberRef cusg = CFNumberCreate(NULL, kCFNumberIntType, &usg);
    CFDictionarySetValue(match, CFSTR(kIOHIDVendorIDKey), cvid);
    CFDictionarySetValue(match, CFSTR(kIOHIDProductIDKey), cpid);
    CFDictionarySetValue(match, CFSTR(kIOHIDPrimaryUsagePageKey), cpage);
    CFDictionarySetValue(match, CFSTR(kIOHIDPrimaryUsageKey), cusg);
    CFRelease(cvid); CFRelease(cpid); CFRelease(cpage); CFRelease(cusg);

    IOHIDManagerSetDeviceMatching(g_manager, match);
    CFRelease(match);
    IOHIDManagerRegisterDeviceMatchingCallback(g_manager, matched_cb, NULL);

    pthread_create(&g_thread, NULL, runloop_fn, NULL);

    printf("Waiting for SR160...\n");
    for (int i = 0; i < 30 && !g_device; i++) usleep(100000);

    if (!g_device) {
        printf("Device not found. Is SR160 connected via USB?\n");
        return 1;
    }
    printf("Connected!\n");

    /* GetPower command: BB 00 B7 00 00 B7 7E */
    uint8_t cmd_getpower[] = { 0xBB, 0x00, 0xB7, 0x00, 0x00, 0xB7, 0x7E };

    /* Format 1: Raw frame at offset 0, pad with zeros */
    {
        uint8_t buf[64] = {0};
        memcpy(buf, cmd_getpower, sizeof(cmd_getpower));
        send_and_wait("Format 1: Raw BB..7E at offset 0", 0, buf, 64);
    }

    /* Format 2: Length prefix: [len] [frame...] */
    {
        uint8_t buf[64] = {0};
        buf[0] = sizeof(cmd_getpower);
        memcpy(buf + 1, cmd_getpower, sizeof(cmd_getpower));
        send_and_wait("Format 2: [len][BB..7E] at offset 0", 0, buf, 64);
    }

    /* Format 3: Zero prefix: [0x00] [frame...] */
    {
        uint8_t buf[64] = {0};
        buf[0] = 0x00;
        memcpy(buf + 1, cmd_getpower, sizeof(cmd_getpower));
        send_and_wait("Format 3: [0x00][BB..7E]", 0, buf, 64);
    }

    /* Format 4: With report ID 1 */
    {
        uint8_t buf[64] = {0};
        memcpy(buf, cmd_getpower, sizeof(cmd_getpower));
        send_and_wait("Format 4: Raw frame, reportID=1", 1, buf, 64);
    }

    /* Format 5: With report ID 2 */
    {
        uint8_t buf[64] = {0};
        memcpy(buf, cmd_getpower, sizeof(cmd_getpower));
        send_and_wait("Format 5: Raw frame, reportID=2", 2, buf, 64);
    }

    /* Format 6: Byte-stuffed — some HID devices need first byte as report ID */
    {
        uint8_t buf[64] = {0};
        buf[0] = 0x00; /* report ID 0 */
        memcpy(buf + 1, cmd_getpower, sizeof(cmd_getpower));
        send_and_wait("Format 6: [reportID=0][frame], reportID=0", 0, buf, 64);
    }

    /* Format 7: Try 63 bytes instead of 64 */
    {
        uint8_t buf[63] = {0};
        memcpy(buf, cmd_getpower, sizeof(cmd_getpower));
        send_and_wait("Format 7: 63 bytes", 0, buf, 63);
    }

    printf("\nDone. Cleaning up...\n");

    if (g_device) {
        IOHIDDeviceUnscheduleFromRunLoop(g_device, g_runloop, kCFRunLoopDefaultMode);
        IOHIDDeviceClose(g_device, kIOHIDOptionsTypeNone);
    }
    CFRunLoopStop(g_runloop);
    pthread_join(g_thread, NULL);
    IOHIDManagerClose(g_manager, kIOHIDOptionsTypeNone);
    CFRelease(g_manager);

    return 0;
}
