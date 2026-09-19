using System.Net.Http;
using Newtonsoft.Json;

namespace Wordsmith.Helpers;

internal sealed class Git
{
    private const string _MANIFEST_JSON_URL = "https://raw.githubusercontent.com/MythicPalette/WordsmithDictionaries/main/manifest.json";
    private const string _LIBRARY_FILE_URL = "https://raw.githubusercontent.com/MythicPalette/WordsmithDictionaries/main/library";

    internal static WebManifest GetManifest()
    {
        // Download the manifest to a string.
        WebManifest result = new();
        using ( HttpClient client = new() )
        {
            int tries = 3;
            // Same as below: asking for anything modified since this instant gets
            // "not modified" back, which throws. Ask not to serve from cache.
            client.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
            while ( tries-- > 0 )
            {
                string raw = "";
                try
                {
                    raw = client.GetStringAsync( _MANIFEST_JSON_URL ).Result;

                    // Deserialize the manifest.
                    WebManifest? manifest = JsonConvert.DeserializeObject<WebManifest>(raw);

                    // If a valid manifest was received then make it the result.
                    if ( manifest != null )
                        result = manifest;

                    result.IsLoaded = true;

                    // Break from the while loop to avoid trying more times.
                    break;
                }
                catch ( Exception e )
                {
                    // Disable the IfModifiedSince header to avoid a 304 response error.
                    client.DefaultRequestHeaders.CacheControl = null;
                    Wordsmith.PluginLog.Warning( $"Failed to get manifest. Tries remaining {tries}. Error: {e.Message}\nRaw: {raw}" );
                }
            }
        }
        return result;
    }

    internal static string[] LoadDictionary(string name)
    {
        // Load the dictionary file as a string
        string result = "";
        using ( HttpClient client = new() )
        {
            // Asking for anything modified since this instant is a request the
            // server answers with "not modified", which is not a success code and
            // so throws. It was meant to force a refresh; it guaranteed a failed
            // first attempt and a warning in the log on every startup. Saying not
            // to serve from cache is the header that actually does that.
            client.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
            int tries = 3;
            while ( tries-- > 0 )
            {
                try
                {
                    // Get data
                    result = client.GetStringAsync( $"{_LIBRARY_FILE_URL}/{name}" ).Result;
                    Wordsmith.PluginLog.Debug( $"Loaded dictionary {name} from web." );
                    break;
                }
                catch ( Exception e )
                {
                    // Disable refresh request.
                    client.DefaultRequestHeaders.CacheControl = null;
                    Wordsmith.PluginLog.Warning( $"Error loading dictionary from web: {e.Message}" );
                }
            }            
        }
        return result.Split( '\n' );
    }
}
