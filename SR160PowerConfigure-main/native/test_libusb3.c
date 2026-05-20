/*
 * Deep dive: USB reset + HID report descriptor dump + retry
 */
#include <libusb-1.0/libusb.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#define SR160_VID  0x2047
#define SR160_PID  0x0301

static void hex_dump(const char *l, const unsigned char *d, int n) {
    printf("%s [%d]: ", l, n);
    for (int i = 0; i < n; i++) printf("%02X ", d[i]);
    printf("\n");
}

int main(void)
{
    libusb_context *ctx;
    libusb_init(&ctx);
    // libusb_set_option(ctx, LIBUSB_OPTION_LOG_LEVEL, LIBUSB_LOG_LEVEL_DEBUG);

    libusb_device_handle *dev = libusb_open_device_with_vid_pid(ctx, SR160_VID, SR160_PID);
    if (!dev) { printf("Not found\n"); return 1; }

    /* 1. Get HID report descriptor via control transfer */
    printf("=== HID Report Descriptor (Interface 0) ===\n");
    unsigned char desc[4096];
    int len = libusb_control_transfer(dev,
        0x81,   /* bmRequestType: device-to-host, standard, interface */
        0x06,   /* bRequest: GET_DESCRIPTOR */
        0x2200, /* wValue: HID Report descriptor type (0x22), index 0 */
        0,      /* wIndex: interface 0 */
        desc, sizeof(desc), 3000);
    if (len > 0) {
        hex_dump("HID desc", desc, len);
    } else {
        printf("GET_DESCRIPTOR failed: %s (rc=%d)\n", libusb_error_name(len), len);
    }

    /* 2. USB reset */
    printf("\n=== USB Reset ===\n");
    int rc = libusb_reset_device(dev);
    printf("Reset: %s\n", libusb_error_name(rc));
    if (rc == LIBUSB_ERROR_NOT_FOUND) {
        printf("Device re-enumerated, reopening...\n");
        dev = libusb_open_device_with_vid_pid(ctx, SR160_VID, SR160_PID);
        if (!dev) { printf("Cannot reopen\n"); return 1; }
    }
    usleep(500000);

    /* 3. Detach and claim Interface 0 */
    libusb_set_auto_detach_kernel_driver(dev, 1);
    libusb_detach_kernel_driver(dev, 0);
    rc = libusb_claim_interface(dev, 0);
    printf("Claim: %s\n", libusb_error_name(rc));
    if (rc < 0) { libusb_close(dev); libusb_exit(ctx); return 1; }

    libusb_clear_halt(dev, 0x01);
    libusb_clear_halt(dev, 0x82);

    /* 4. Try read first — maybe device sends unsolicited data */
    printf("\n=== Pre-read (check for unsolicited data) ===\n");
    {
        unsigned char rx[64] = {0};
        int transferred = 0;
        rc = libusb_interrupt_transfer(dev, 0x82, rx, 64, &transferred, 1000);
        printf("Pre-read: %s (transferred=%d)\n", libusb_error_name(rc), transferred);
        if (transferred > 0) hex_dump("RX", rx, transferred);
    }

    /* 5. Send GetPower and try read */
    printf("\n=== GetPower after reset ===\n");
    {
        unsigned char cmd[64] = {0};
        cmd[0]=0xBB; cmd[1]=0x00; cmd[2]=0xB7; cmd[3]=0x00;
        cmd[4]=0x00; cmd[5]=0xB7; cmd[6]=0x7E;

        int transferred = 0;
        hex_dump("TX", cmd, 7);
        rc = libusb_interrupt_transfer(dev, 0x01, cmd, 64, &transferred, 3000);
        printf("Write: %s (transferred=%d)\n", libusb_error_name(rc), transferred);

        if (rc == 0) {
            unsigned char rx[64] = {0};
            transferred = 0;
            rc = libusb_interrupt_transfer(dev, 0x82, rx, 64, &transferred, 5000);
            printf("Read (5s): %s (transferred=%d)\n", libusb_error_name(rc), transferred);
            if (transferred > 0) hex_dump("RX", rx, transferred);
        }
    }

    /* 6. Try with different frame formats */
    printf("\n=== Format: [0x07][BB...7E] length-prefixed ===\n");
    {
        unsigned char cmd[64] = {0};
        cmd[0]=0x07; /* length */
        cmd[1]=0xBB; cmd[2]=0x00; cmd[3]=0xB7; cmd[4]=0x00;
        cmd[5]=0x00; cmd[6]=0xB7; cmd[7]=0x7E;

        int transferred = 0;
        rc = libusb_interrupt_transfer(dev, 0x01, cmd, 64, &transferred, 3000);
        printf("Write: %s\n", libusb_error_name(rc));
        if (rc == 0) {
            unsigned char rx[64] = {0};
            transferred = 0;
            rc = libusb_interrupt_transfer(dev, 0x82, rx, 64, &transferred, 3000);
            printf("Read: %s (transferred=%d)\n", libusb_error_name(rc), transferred);
            if (transferred > 0) hex_dump("RX", rx, transferred);
        }
    }

    printf("\n=== Format: just [B7] (bare command byte) ===\n");
    {
        unsigned char cmd[64] = {0};
        cmd[0]=0xB7;

        int transferred = 0;
        rc = libusb_interrupt_transfer(dev, 0x01, cmd, 64, &transferred, 3000);
        printf("Write: %s\n", libusb_error_name(rc));
        if (rc == 0) {
            unsigned char rx[64] = {0};
            transferred = 0;
            rc = libusb_interrupt_transfer(dev, 0x82, rx, 64, &transferred, 3000);
            printf("Read: %s (transferred=%d)\n", libusb_error_name(rc), transferred);
            if (transferred > 0) hex_dump("RX", rx, transferred);
        }
    }

    libusb_release_interface(dev, 0);
    libusb_close(dev);
    libusb_exit(ctx);
    printf("\nDone.\n");
    return 0;
}
