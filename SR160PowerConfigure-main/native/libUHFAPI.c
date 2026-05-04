/*
 * libUHFAPI — macOS implementation for Chainway SR160
 *
 * Strategy: IOKit HID with SEIZE (exclusive) on Interface 0 (UsagePage 0x8C).
 * Send via IOHIDDeviceSetReport (Output/Feature).
 * Receive via IOHIDDeviceGetReport (synchronous) — polls for response.
 * Also try async callback as secondary path.
 *
 * Interface 1 (HID keyboard for EPC) is a separate IOHIDDevice — unaffected.
 */

#include <IOKit/hid/IOHIDManager.h>
#include <CoreFoundation/CoreFoundation.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <stdarg.h>
#include <pthread.h>

/* ── Chainway SR160 ── */
#define SR160_VID  0x2047
#define SR160_PID  0x0301
#define CMD_USAGE_PAGE  0x8C
#define CMD_USAGE       0x01

/* ── Protocol ── */
#define FRAME_HEAD      0xBB
#define FRAME_END       0x7E
#define HID_REPORT_LEN  64
#define TIMEOUT_SEC     3

#define CMD_GET_POWER       0xB7
#define CMD_SET_POWER       0xB6
#define CMD_GET_ANT_POWER   0xB3
#define CMD_SET_ANT_POWER   0xB4
#define CMD_GET_VERSION     0x03
#define CMD_INVENTORY       0x22
#define CMD_STOP            0x28

/* ── State ── */
static IOHIDManagerRef  g_manager = NULL;
static IOHIDDeviceRef   g_device  = NULL;
static CFRunLoopRef     g_runloop = NULL;
static pthread_t        g_thread;
static int              g_thread_running = 0;
static int              g_last_error = 0;

/* ── Async response buffer ── */
static pthread_mutex_t  g_rx_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t   g_rx_cond  = PTHREAD_COND_INITIALIZER;
static uint8_t          g_rx_buf[HID_REPORT_LEN];
static int              g_rx_len = 0;
static int              g_rx_ready = 0;

/* ── Inventory ring buffer ── */
#define RX_RING_SIZE 4096
static uint8_t g_rx_ring[RX_RING_SIZE];
static int     g_rx_head = 0;
static int     g_rx_tail = 0;

/* ── Debug logging ── */
static FILE *g_logfile = NULL;

static void dbg(const char *fmt, ...)
{
    if (!g_logfile) {
        const char *home = getenv("HOME");
        char path[512];
        snprintf(path, sizeof(path), "%s/sr160_debug.log", home ? home : "/tmp");
        g_logfile = fopen(path, "a");
        if (!g_logfile) return;
    }
    va_list ap;
    va_start(ap, fmt);
    vfprintf(g_logfile, fmt, ap);
    va_end(ap);
    fprintf(g_logfile, "\n");
    fflush(g_logfile);
}

static void dbg_hex(const char *prefix, const uint8_t *data, int len)
{
    if (!g_logfile) dbg("init");
    if (!g_logfile) return;
    fprintf(g_logfile, "%s [%d bytes]: ", prefix, len);
    for (int i = 0; i < len && i < 64; i++)
        fprintf(g_logfile, "%02X ", data[i]);
    fprintf(g_logfile, "\n");
    fflush(g_logfile);
}

/* ──────────────────────── Helpers ──────────────────────── */

static uint8_t calc_checksum(const uint8_t *buf, int len)
{
    uint8_t cs = 0;
    for (int i = 0; i < len; i++) cs += buf[i];
    return cs;
}

static int build_frame(uint8_t *out, uint8_t type, uint8_t cmd,
                       const uint8_t *data, int dlen)
{
    int idx = 0;
    out[idx++] = FRAME_HEAD;
    out[idx++] = type;
    out[idx++] = cmd;
    out[idx++] = (uint8_t)((dlen >> 8) & 0xFF);
    out[idx++] = (uint8_t)(dlen & 0xFF);
    if (data && dlen > 0) memcpy(out + idx, data, dlen);
    idx += dlen;
    out[idx] = calc_checksum(out + 1, idx - 1);
    idx++;
    out[idx++] = FRAME_END;
    return idx;
}

static int parse_response(const uint8_t *rx, int rxlen,
                          uint8_t *data_out, int *data_len)
{
    int start = -1;
    for (int i = 0; i < rxlen; i++) {
        if (rx[i] == FRAME_HEAD) { start = i; break; }
    }
    if (start < 0 || rxlen - start < 7) return -1;

    const uint8_t *f = rx + start;
    int pl = (f[3] << 8) | f[4];

    if (data_out && data_len) {
        int copy = pl < *data_len ? pl : *data_len;
        if (copy > 0) memcpy(data_out, f + 5, copy);
        *data_len = copy;
    }
    return 0;
}

/* ── HID callbacks ── */

static void input_report_callback(void *context, IOReturn result,
                                   void *sender, IOHIDReportType type,
                                   uint32_t reportID, uint8_t *report,
                                   CFIndex reportLength)
{
    (void)context; (void)sender; (void)type;
    dbg("ASYNC callback: result=0x%08X, id=%u, len=%ld",
        (unsigned)result, (unsigned)reportID, (long)reportLength);
    dbg_hex("  ASYNC RX", report, (int)reportLength);

    pthread_mutex_lock(&g_rx_mutex);
    int len = (int)reportLength;
    if (len > HID_REPORT_LEN) len = HID_REPORT_LEN;
    memcpy(g_rx_buf, report, len);
    g_rx_len = len;
    g_rx_ready = 1;
    pthread_cond_signal(&g_rx_cond);
    pthread_mutex_unlock(&g_rx_mutex);
}

static void device_matched_callback(void *context, IOReturn result,
                                     void *sender, IOHIDDeviceRef device)
{
    (void)context; (void)result; (void)sender;
    if (g_device) return;

    dbg("device_matched: attempting seize...");

    IOReturn rc = IOHIDDeviceOpen(device, kIOHIDOptionsTypeSeizeDevice);
    dbg("  IOHIDDeviceOpen(seize): 0x%08X", (unsigned)rc);
    if (rc != kIOReturnSuccess) {
        rc = IOHIDDeviceOpen(device, kIOHIDOptionsTypeNone);
        dbg("  IOHIDDeviceOpen(shared): 0x%08X", (unsigned)rc);
        if (rc != kIOReturnSuccess) {
            g_last_error = (int)rc;
            return;
        }
    }

    g_device = device;

    /* Explicitly schedule device on this RunLoop */
    IOHIDDeviceScheduleWithRunLoop(device, CFRunLoopGetCurrent(), kCFRunLoopDefaultMode);
    dbg("  device scheduled on RunLoop");

    /* Register async input report callback */
    static uint8_t report_buf[HID_REPORT_LEN];
    IOHIDDeviceRegisterInputReportCallback(
        device, report_buf, sizeof(report_buf),
        input_report_callback, NULL);
    dbg("  callback registered, device ready");

    /* Enumerate HID reports for debugging */
    CFTypeRef maxInRef = IOHIDDeviceGetProperty(device, CFSTR(kIOHIDMaxInputReportSizeKey));
    CFTypeRef maxOutRef = IOHIDDeviceGetProperty(device, CFSTR(kIOHIDMaxOutputReportSizeKey));
    CFTypeRef maxFeatRef = IOHIDDeviceGetProperty(device, CFSTR(kIOHIDMaxFeatureReportSizeKey));
    int maxIn = 0, maxOut = 0, maxFeat = 0;
    if (maxInRef) CFNumberGetValue(maxInRef, kCFNumberIntType, &maxIn);
    if (maxOutRef) CFNumberGetValue(maxOutRef, kCFNumberIntType, &maxOut);
    if (maxFeatRef) CFNumberGetValue(maxFeatRef, kCFNumberIntType, &maxFeat);
    dbg("  maxInput=%d, maxOutput=%d, maxFeature=%d", maxIn, maxOut, maxFeat);
}

static void device_removed_callback(void *context, IOReturn result,
                                     void *sender, IOHIDDeviceRef device)
{
    (void)context; (void)result; (void)sender;
    if (g_device == device) {
        dbg("device_removed");
        g_device = NULL;
    }
}

/* ── RunLoop thread ── */
static void *runloop_thread(void *arg)
{
    (void)arg;
    g_runloop = CFRunLoopGetCurrent();
    IOHIDManagerScheduleWithRunLoop(g_manager, g_runloop, kCFRunLoopDefaultMode);
    IOHIDManagerOpen(g_manager, kIOHIDOptionsTypeNone);
    dbg("RunLoop started");
    CFRunLoopRun();
    dbg("RunLoop exited");
    g_runloop = NULL;
    return NULL;
}

/* ── Send and receive ── */
static int transact(const uint8_t *frame, int framelen,
                    uint8_t *rx, int rxmax, int *rxlen)
{
    if (!g_device) return -1;

    uint8_t report[HID_REPORT_LEN];
    memset(report, 0, sizeof(report));
    int copy = framelen < HID_REPORT_LEN ? framelen : HID_REPORT_LEN;
    memcpy(report, frame, copy);

    /* Clear async buffer */
    pthread_mutex_lock(&g_rx_mutex);
    g_rx_ready = 0;
    pthread_mutex_unlock(&g_rx_mutex);

    dbg_hex("TX", report, HID_REPORT_LEN);

    /* Send: try Output report, then Feature */
    IOReturn ret = IOHIDDeviceSetReport(g_device,
        kIOHIDReportTypeOutput, 0, report, sizeof(report));
    dbg("SetReport(Output,0): 0x%08X", (unsigned)ret);

    if (ret != kIOReturnSuccess) {
        ret = IOHIDDeviceSetReport(g_device,
            kIOHIDReportTypeFeature, 0, report, sizeof(report));
        dbg("SetReport(Feature,0): 0x%08X", (unsigned)ret);
    }

    g_last_error = (int)ret;
    if (ret != kIOReturnSuccess) return -2;

    /* Method 1: Check async callback (quick check — 500ms) */
    pthread_mutex_lock(&g_rx_mutex);
    if (!g_rx_ready) {
        struct timespec ts;
        clock_gettime(CLOCK_REALTIME, &ts);
        ts.tv_nsec += 500000000; /* 500ms */
        if (ts.tv_nsec >= 1000000000) { ts.tv_sec++; ts.tv_nsec -= 1000000000; }
        pthread_cond_timedwait(&g_rx_cond, &g_rx_mutex, &ts);
    }
    if (g_rx_ready) {
        int n = g_rx_len < rxmax ? g_rx_len : rxmax;
        memcpy(rx, g_rx_buf, n);
        if (rxlen) *rxlen = n;
        g_rx_ready = 0;
        pthread_mutex_unlock(&g_rx_mutex);
        dbg("Got response via ASYNC callback (%d bytes)", n);
        return 0;
    }
    pthread_mutex_unlock(&g_rx_mutex);
    dbg("Async callback timeout, trying GetReport...");

    /* Method 2: Synchronous GetReport — try various report IDs and types */
    IOHIDReportType rtypes[] = { kIOHIDReportTypeInput, kIOHIDReportTypeFeature };
    const char *rnames[] = { "Input", "Feature" };
    int rids[] = { 0, 1, 2, 3 };

    for (int t = 0; t < 2; t++) {
        for (int r = 0; r < 4; r++) {
            uint8_t rx_buf[HID_REPORT_LEN];
            CFIndex rx_len = HID_REPORT_LEN;
            memset(rx_buf, 0, sizeof(rx_buf));

            ret = IOHIDDeviceGetReport(g_device,
                rtypes[t], rids[r], rx_buf, &rx_len);

            if (ret == kIOReturnSuccess && rx_len > 0) {
                /* Check for Chainway frame */
                int has_bb = 0;
                for (int i = 0; i < (int)rx_len; i++) {
                    if (rx_buf[i] == FRAME_HEAD) { has_bb = 1; break; }
                }
                dbg("GetReport(%s,id=%d): OK, len=%ld, has_BB=%d",
                    rnames[t], rids[r], (long)rx_len, has_bb);
                if (has_bb || rx_len > 0) {
                    dbg_hex("  RX", rx_buf, (int)rx_len);
                }
                if (has_bb) {
                    int n = (int)rx_len < rxmax ? (int)rx_len : rxmax;
                    memcpy(rx, rx_buf, n);
                    if (rxlen) *rxlen = n;
                    g_last_error = 0;
                    return 0;
                }
            } else if (ret != kIOReturnSuccess) {
                dbg("GetReport(%s,id=%d): 0x%08X", rnames[t], rids[r], (unsigned)ret);
            }
        }
    }

    dbg("All receive methods failed");
    g_last_error = 0;
    return -3;
}

/* ──────────────────────── Public API ──────────────────────── */

__attribute__((visibility("default")))
int UsbOpen(void)
{
    if (g_device) return 0;

    dbg("=== UsbOpen() — IOKit HID seize + GetReport ===");

    g_manager = IOHIDManagerCreate(kCFAllocatorDefault, kIOHIDOptionsTypeNone);
    if (!g_manager) return -1;

    CFMutableDictionaryRef match = CFDictionaryCreateMutable(
        kCFAllocatorDefault, 4,
        &kCFTypeDictionaryKeyCallBacks,
        &kCFTypeDictionaryValueCallBacks);

    int vid = SR160_VID, pid = SR160_PID;
    int usage_page = CMD_USAGE_PAGE, usage = CMD_USAGE;

    CFNumberRef cfVid = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &vid);
    CFNumberRef cfPid = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &pid);
    CFNumberRef cfPage = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &usage_page);
    CFNumberRef cfUsage = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &usage);

    CFDictionarySetValue(match, CFSTR(kIOHIDVendorIDKey), cfVid);
    CFDictionarySetValue(match, CFSTR(kIOHIDProductIDKey), cfPid);
    CFDictionarySetValue(match, CFSTR(kIOHIDPrimaryUsagePageKey), cfPage);
    CFDictionarySetValue(match, CFSTR(kIOHIDPrimaryUsageKey), cfUsage);

    CFRelease(cfVid); CFRelease(cfPid);
    CFRelease(cfPage); CFRelease(cfUsage);

    IOHIDManagerSetDeviceMatching(g_manager, match);
    CFRelease(match);

    IOHIDManagerRegisterDeviceMatchingCallback(g_manager, device_matched_callback, NULL);
    IOHIDManagerRegisterDeviceRemovalCallback(g_manager, device_removed_callback, NULL);

    g_thread_running = 1;
    pthread_create(&g_thread, NULL, runloop_thread, NULL);

    for (int i = 0; i < 30 && !g_device; i++)
        usleep(100000);

    if (!g_device) {
        dbg("Device not found");
        if (g_runloop) CFRunLoopStop(g_runloop);
        g_thread_running = 0;
        pthread_join(g_thread, NULL);
        IOHIDManagerClose(g_manager, kIOHIDOptionsTypeNone);
        CFRelease(g_manager);
        g_manager = NULL;
        return -2;
    }

    g_rx_head = g_rx_tail = 0;
    dbg("UsbOpen: success");
    return 0;
}

__attribute__((visibility("default")))
void UsbClose(void)
{
    dbg("=== UsbClose() ===");
    if (g_device) {
        IOHIDDeviceUnscheduleFromRunLoop(g_device, g_runloop, kCFRunLoopDefaultMode);
        IOHIDDeviceClose(g_device, kIOHIDOptionsTypeNone);
        g_device = NULL;
    }
    if (g_runloop) {
        CFRunLoopStop(g_runloop);
    }
    if (g_thread_running) {
        g_thread_running = 0;
        pthread_join(g_thread, NULL);
    }
    if (g_manager) {
        IOHIDManagerClose(g_manager, kIOHIDOptionsTypeNone);
        CFRelease(g_manager);
        g_manager = NULL;
    }
}

__attribute__((visibility("default")))
int UHFGetLastIOReturn(void) { return g_last_error; }

__attribute__((visibility("default")))
int UHFGetPower(uint8_t *uPower)
{
    if (!g_device || !uPower) return -1;
    uint8_t frame[64], rx[HID_REPORT_LEN];
    int framelen = build_frame(frame, 0x00, CMD_GET_POWER, NULL, 0);
    int rxlen = 0;
    dbg("UHFGetPower");
    if (transact(frame, framelen, rx, sizeof(rx), &rxlen) != 0) return -2;
    uint8_t data[32]; int dlen = sizeof(data);
    if (parse_response(rx, rxlen, data, &dlen) != 0) return -3;
    if (dlen >= 1) { *uPower = data[0]; dbg("  power=%d", data[0]); }
    else return -4;
    return 0;
}

__attribute__((visibility("default")))
int UHFSetPower(uint8_t save, uint8_t uPower)
{
    if (!g_device) return -1;
    uint8_t payload[2] = { save, uPower };
    uint8_t frame[64], rx[HID_REPORT_LEN];
    int framelen = build_frame(frame, 0x00, CMD_SET_POWER, payload, 2);
    int rxlen = 0;
    dbg("UHFSetPower: save=%d power=%d", save, uPower);
    if (transact(frame, framelen, rx, sizeof(rx), &rxlen) != 0) return -2;
    uint8_t data[32]; int dlen = sizeof(data);
    if (parse_response(rx, rxlen, data, &dlen) != 0) return -3;
    return 0;
}

__attribute__((visibility("default")))
int UHFGetAntennaPower(uint8_t *ppower, int *nBytesReturned)
{
    if (!g_device || !ppower || !nBytesReturned) return -1;
    uint8_t frame[64], rx[HID_REPORT_LEN];
    int framelen = build_frame(frame, 0x00, CMD_GET_ANT_POWER, NULL, 0);
    int rxlen = 0;
    if (transact(frame, framelen, rx, sizeof(rx), &rxlen) != 0) return -2;
    uint8_t data[64]; int dlen = sizeof(data);
    if (parse_response(rx, rxlen, data, &dlen) != 0) return -3;
    int copy = dlen < *nBytesReturned ? dlen : *nBytesReturned;
    memcpy(ppower, data, copy);
    *nBytesReturned = copy;
    return 0;
}

__attribute__((visibility("default")))
int UHFSetAntennaPower(uint8_t save, uint8_t num,
                       uint8_t read_power, uint8_t write_power)
{
    if (!g_device) return -1;
    uint8_t payload[4] = { save, num, read_power, write_power };
    uint8_t frame[64], rx[HID_REPORT_LEN];
    int framelen = build_frame(frame, 0x00, CMD_SET_ANT_POWER, payload, 4);
    int rxlen = 0;
    if (transact(frame, framelen, rx, sizeof(rx), &rxlen) != 0) return -2;
    return 0;
}

static int get_version(uint8_t subcmd, uint8_t *version)
{
    if (!g_device || !version) return -1;
    uint8_t payload[1] = { subcmd };
    uint8_t frame[64], rx[HID_REPORT_LEN];
    int framelen = build_frame(frame, 0x00, CMD_GET_VERSION, payload, 1);
    int rxlen = 0;
    if (transact(frame, framelen, rx, sizeof(rx), &rxlen) != 0) return -2;
    uint8_t data[64]; int dlen = sizeof(data);
    if (parse_response(rx, rxlen, data, &dlen) != 0) return -3;
    memcpy(version, data, dlen);
    return 0;
}

__attribute__((visibility("default")))
int UHFGetHardwareVersion(uint8_t *v) { return get_version(0x00, v); }
__attribute__((visibility("default")))
int UHFGetSoftwareVersion(uint8_t *v) { return get_version(0x01, v); }
__attribute__((visibility("default")))
int UHFGetReaderVersion(uint8_t *v) { return get_version(0x02, v); }

__attribute__((visibility("default")))
int UHFInventory(void)
{
    if (!g_device) return -1;
    g_rx_head = g_rx_tail = 0;
    uint8_t frame[64], rx[HID_REPORT_LEN];
    int framelen = build_frame(frame, 0x00, CMD_INVENTORY, NULL, 0);
    int rxlen = 0;
    if (transact(frame, framelen, rx, sizeof(rx), &rxlen) != 0) return -2;
    for (int i = 0; i < rxlen && g_rx_head < RX_RING_SIZE; i++)
        g_rx_ring[g_rx_head++] = rx[i];
    return 0;
}

__attribute__((visibility("default")))
int UHFStopGet(void)
{
    if (!g_device) return -1;
    uint8_t frame[64], rx[HID_REPORT_LEN];
    int framelen = build_frame(frame, 0x00, CMD_STOP, NULL, 0);
    int rxlen = 0;
    transact(frame, framelen, rx, sizeof(rx), &rxlen);
    return 0;
}

__attribute__((visibility("default")))
int UHF_GetReceived_EX(int *uLenUii, uint8_t *uUii)
{
    if (!uLenUii || !uUii) return -1;
    while (g_rx_tail < g_rx_head) {
        if (g_rx_ring[g_rx_tail] != FRAME_HEAD) { g_rx_tail++; continue; }
        int avail = g_rx_head - g_rx_tail;
        if (avail < 7) break;
        int pl = (g_rx_ring[g_rx_tail + 3] << 8) | g_rx_ring[g_rx_tail + 4];
        int frame_len = 5 + pl + 2;
        if (avail < frame_len) break;
        if (g_rx_ring[g_rx_tail + frame_len - 1] == FRAME_END) {
            int copy = pl < *uLenUii ? pl : *uLenUii;
            memcpy(uUii, &g_rx_ring[g_rx_tail + 5], copy);
            *uLenUii = copy;
            g_rx_tail += frame_len;
            return 0;
        }
        g_rx_tail++;
    }
    *uLenUii = 0;
    return -1;
}
