using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace ServerPickerX.Services.Versions
{
    public interface IVersionService
    {
        Task CheckVersionAsync();

        // What the last check found for the download button in the footer, or null
        AvailableRelease? AvailableUpdate { get; }
    }
}