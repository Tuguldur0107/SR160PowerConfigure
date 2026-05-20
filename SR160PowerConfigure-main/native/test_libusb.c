/*
 * Test SR160 communication via libusb — direct interrupt endpoint access.
 * Bypasses macOS HID driver entirely.
 */
#include <libusb-1.0/libusb.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

#define SR160_VID  0x2047
#define SR160_PID  0x0301
#define IFACE_NUM  0
#define EP_OUT     0x01
#define EP_IN      0x82
#define TIMEOUT_MS 3000

static void hex_dump(const char *label, const unsigned char *data, int len)
{
    printf("%s [%d bytes]: ", label, len);
    for (int i = 0; i < len && i < 64; i++) printf("%02X ", data[i]);
    printf("\n");
}

int main(void)
{
    libusb_context *ctx = NULL;
    libusb_device_handle *dev = NULL;
    int rc;

    rc = libusb_init(&ctx);
    if (rc < 0) { printf("libusb_init failed: %s\n", libusb_error_name(rc)); return 1; }

    dev = libusb_open_device_with_vid_pid(ctx, SR160_VID, SR160_PID);
    if (!dev) { printf("Device not found (VID=%04X PID=%04X)\n", SR160_VID, SR160_PID); return 1; }
    printf("Device opened!\n");

    /* Try auto-detach first */
    rc = libusb_set_auto_detach_kernel_driver(dev, 1);
    printf("Auto-detach: %s\n", libusb_error_name(rc));

    /* Claim Interface 0 */
    rc = libusb_claim_interface(dev, IFACE_NUM);
    printf("Claim interface %d: %s\n", IFACE_NUM, libusb_error_name(rc));

    if (rc < 0) {
        /* Try manual detach */
        printf("Trying manual detach...\n");
        if (libusb_kernel_driver_active(dev, IFACE_NUM) == 1) {
            rc = libusb_detach_kernel_driver(dev, IFACE_NUM);
            printf("  detach: %s\n", libusb_error_name(rc));
            rc = libusb_claim_interface(dev, IFACE_NUM);
            printf("  claim after detach: %s\n", libusb_error_name(rc));
        }
    }

    if (rc < 0) {
        printf("Cannot claim interface.\n");
        libusb_close(dev);
        libusb_exit(ctx);
        return 1;
    }

    /* GetPower command: BB 00 B7 00 00 B7 7E */
    unsigned char cmd[64] = {0};
    cmd[0] = 0xBB;
    cmd[1] = 0x00;
    cmd[2] = 0xB7;
    cmd[3] = 0x00;
    cmd[4] = 0x00;
    cmd[5] = 0xB7;
    cmd[6] = 0x7E;

    int transferred = 0;

    hex_dump("TX", cmd, 64);
    rc = libusb_interrupt_transfer(dev, EP_OUT, cmd, 64, &transferred, TIMEOUT_MS);
    printf("Write EP 0x%02X: %s, transferred=%d\n", EP_OUT, libusb_error_name(rc), transferred);

    if (rc == 0) {
        unsigned char rx[64] = {0};
        transferred = 0;
        rc = libusb_interrupt_transfer(dev, EP_IN, rx, 64, &transferred, TIMEOUT_MS);
        printf("Read EP 0x%02X: %s, transferred=%d\n", EP_IN, libusb_error_name(rc), transferred);

        if (rc == 0 && transferred > 0) {
            hex_dump("RX", rx, transferred);

            /* Parse: look for BB */
            for (int i = 0; i < transferred; i++) {
                if (rx[i] == 0xBB && i + 6 < transferred) {
                    int pl = (rx[i+3] << 8) | rx[i+4];
                    printf("Found Chainway frame: cmd=0x%02X, payload_len=%d\n",
                           rx[i+2], pl);
                    if (pl >= 1) {
                        printf("Power value: %d dBm\n", rx[i+5]);
                    }
                    break;
                }
            }
        }
    }

    /* Also test SetPower: BB 00 B6 00 02 01 14 CD 7E */
    printf("\n--- SetPower(save=1, power=20) ---\n");
    memset(cmd, 0, 64);
    cmd[0] = 0xBB; cmd[1] = 0x00; cmd[2] = 0xB6;
    cmd[3] = 0x00; cmd[4] = 0x02; /* payload len = 2 */
    cmd[5] = 0x01; cmd[6] = 0x14; /* save=1, power=20 */
    cmd[7] = 0x00 + 0xB6 + 0x00 + 0x02 + 0x01 + 0x14; /* checksum */
    cmd[8] = 0x7E;

    hex_dump("TX", cmd, 64);
    rc = libusb_interrupt_transfer(dev, EP_OUT, cmd, 64, &transferred, TIMEOUT_MS);
    printf("Write: %s, transferred=%d\n", libusb_error_name(rc), transferred);

    if (rc == 0) {
        unsigned char rx[64] = {0};
        transferred = 0;
        rc = libusb_interrupt_transfer(dev, EP_IN, rx, 64, &transferred, TIMEOUT_MS);
        printf("Read: %s, transferred=%d\n", libusb_error_name(rc), transferred);
        if (transferred > 0) hex_dump("RX", rx, transferred);
    }

    libusb_release_interface(dev, IFACE_NUM);
    libusb_close(dev);
    libusb_exit(ctx);
    printf("Done.\n");
    return 0;
}
