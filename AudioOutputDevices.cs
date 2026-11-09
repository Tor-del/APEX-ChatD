using NAudio.CoreAudioApi;

namespace TwitchChatAgent;

internal sealed record AudioOutputChoice(string Id, string Name);

internal static class AudioOutputDevices
{
    public static AudioOutputChoice Default => new("", Ui.T("Устройство по умолчанию"));

    public static IReadOnlyList<AudioOutputChoice> List()
    {
        var result = new List<AudioOutputChoice> { Default };
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device)
                result.Add(new(device.ID, device.FriendlyName));
        return result;
    }

    public static MMDevice Resolve(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            var device = string.IsNullOrWhiteSpace(id)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(id);
            if (device.State == DeviceState.Active)
                return device;
            device.Dispose();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new InvalidOperationException(Ui.T("Устройство вывода недоступно."), ex);
        }
        throw new InvalidOperationException(Ui.T("Устройство вывода недоступно."));
    }
}
