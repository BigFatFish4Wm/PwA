namespace PwA.Coop.Protocol;

public readonly record struct BrokerControllerDeviceAssignment(bool IsConfigured, int? Device)
{
    public static BrokerControllerDeviceAssignment None => new(IsConfigured: true, Device: null);

    public static BrokerControllerDeviceAssignment ForDevice(int device)
    {
        if (device is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(device), "Controller device must be 0 through 3.");
        }

        return new BrokerControllerDeviceAssignment(IsConfigured: true, Device: device);
    }
}
