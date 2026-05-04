/*
 * Detailed libusb test for SR160 — dump endpoints and try communication
 */
#include <libusb-1.0/libusb.h>
#include <stdio.h>
#include <string.h>

#define SR160_VID  0x2047
#define SR160_PID  0x0301

static void hex_dump(const char *label, const unsigned char *d, int len)
{
    printf("%s [%d]: ", label, len);
    for (int i = 0; i < len && i < 32; i++) printf("%02X ", d[i]);
    if (len > 32) printf("...");
    printf("\n");
}

int main(void)
{
    libusb_context *ctx;
    libusb_init(&ctx);

    libusb_device_handle *dev = libusb_open_device_with_vid_pid(ctx, SR160_VID, SR160_PID);
    if (!dev) { printf("Device not found\n"); return 1; }

    libusb_device *udev = libusb_get_device(dev);

    /* Dump all interfaces and endpoints */
    struct libusb_config_descriptor *config;
    libusb_get_active_config_descriptor(udev, &config);
    printf("Configuration: %d interfaces\n", config->bNumInterfaces);

    for (int i = 0; i < config->bNumInterfaces; i++) {
        const struct libusb_interface *iface = &config->interface[i];
        for (int j = 0; j < iface->num_altsetting; j++) {
            const struct libusb_interface_descriptor *alt = &iface->altsetting[j];
            printf("\nInterface %d (alt %d): class=%d subclass=%d protocol=%d endpoints=%d\n",
                   alt->bInterfaceNumber, alt->bAlternateSetting,
                   alt->bInterfaceClass, alt->bInterfaceSubClass,
                   alt->bInterfaceProtocol, alt->bNumEndpoints);

            for (int k = 0; k < alt->bNumEndpoints; k++) {
                const struct libusb_endpoint_descriptor *ep = &alt->endpoint[k];
                const char *dir = (ep->bEndpointAddress & 0x80) ? "IN" : "OUT";
                const char *type = "?";
                switch (ep->bmAttributes & 0x03) {
                    case 0: type = "Control"; break;
                    case 1: type = "Isochronous"; break;
                    case 2: type = "Bulk"; break;
                    case 3: type = "Interrupt"; break;
                }
                printf("  EP 0x%02X: %s %s, maxPacket=%d, interval=%d\n",
                       ep->bEndpointAddress, dir, type,
                       ep->wMaxPacketSize, ep->bInterval);
            }
        }
    }
    libusb_free_config_descriptor(config);

    /* Try to work with Interface 0 */
    printf("\n--- Claiming Interface 0 ---\n");
    libusb_set_auto_detach_kernel_driver(dev, 1);
    if (libusb_kernel_driver_active(dev, 0) == 1) {
        int rc = libusb_detach_kernel_driver(dev, 0);
        printf("Detach: %s\n", libusb_error_name(rc));
    }
    int rc = libusb_claim_interface(dev, 0);
    printf("Claim: %s\n", libusb_error_name(rc));

    if (rc == 0) {
        /* Clear halt on both endpoints */
        printf("Clear halt EP OUT: %s\n", libusb_error_name(libusb_clear_halt(dev, 0x01)));
        printf("Clear halt EP IN: %s\n", libusb_error_name(libusb_clear_halt(dev, 0x82)));

        /* GetPower: BB 00 B7 00 00 B7 7E */
        unsigned char cmd[64] = {0};
        cmd[0]=0xBB; cmd[1]=0x00; cmd[2]=0xB7; cmd[3]=0x00;
        cmd[4]=0x00; cmd[5]=0xB7; cmd[6]=0x7E;

        int transferred = 0;

        printf("\n--- Attempt 1: interrupt_transfer ---\n");
        hex_dump("TX", cmd, 64);
        rc = libusb_interrupt_transfer(dev, 0x01, cmd, 64, &transferred, 3000);
        printf("Write EP 0x01: %s (transferred=%d)\n", libusb_error_name(rc), transferred);

        if (rc == 0) {
            unsigned char rx[64] = {0};
            transferred = 0;
            rc = libusb_interrupt_transfer(dev, 0x82, rx, 64, &transferred, 3000);
            printf("Read EP 0x82: %s (transferred=%d)\n", libusb_error_name(rc), transferred);
            if (transferred > 0) hex_dump("RX", rx, transferred);
        }

        /* Try control transfer SET_REPORT then interrupt read */
        printf("\n--- Attempt 2: control SET_REPORT + interrupt read ---\n");
        rc = libusb_control_transfer(dev,
            0x21,  /* bmRequestType: class, interface, host-to-device */
            0x09,  /* bRequest: SET_REPORT */
            0x0200, /* wValue: report type Output (0x02), report ID 0 */
            0,     /* wIndex: interface 0 */
            cmd, 64, 3000);
        printf("Control SET_REPORT: %s (rc=%d)\n", libusb_error_name(rc < 0 ? rc : 0), rc);

        if (rc >= 0) {
            unsigned char rx[64] = {0};
            transferred = 0;
            rc = libusb_interrupt_transfer(dev, 0x82, rx, 64, &transferred, 3000);
            printf("Read EP 0x82: %s (transferred=%d)\n", libusb_error_name(rc), transferred);
            if (transferred > 0) hex_dump("RX", rx, transferred);
        }

        /* Try bulk transfer (in case endpoints are actually bulk) */
        printf("\n--- Attempt 3: bulk_transfer ---\n");
        transferred = 0;
        rc = libusb_bulk_transfer(dev, 0x01, cmd, 64, &transferred, 3000);
        printf("Bulk write EP 0x01: %s (transferred=%d)\n", libusb_error_name(rc), transferred);

        if (rc == 0) {
            unsigned char rx[64] = {0};
            transferred = 0;
            rc = libusb_bulk_transfer(dev, 0x82, rx, 64, &transferred, 3000);
            printf("Bulk read EP 0x82: %s (transferred=%d)\n", libusb_error_name(rc), transferred);
            if (transferred > 0) hex_dump("RX", rx, transferred);
        }

        libusb_release_interface(dev, 0);
    }

    libusb_close(dev);
    libusb_exit(ctx);
    return 0;
}
