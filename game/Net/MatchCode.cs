// MatchCode.cs — "join by code" without a server behind it.
//
// A code is just the host's IPv4 address and port packed into 6 bytes and
// rendered in Crockford base32: 10 characters, printed as XXXXX-XXXXX. Decoding
// gives the endpoint straight back, so no lobby service, no account, nothing to
// keep running. Two people on the same LAN (or with a forwarded port) can read
// one over a call.
//
// BE HONEST ABOUT THE LIMIT: this is a friendlier spelling of an IP address, not
// NAT traversal. A code containing a private address (192.168.x.x) only works
// for someone on that same network. Punching through home routers needs a
// rendezvous server, which is a separate piece of infrastructure and a separate
// decision — see CONTEXT_HANDOFF.md.
//
// Crockford base32 omits I, L, O and U, so a code can't be misread as digits or
// spell anything unfortunate, and decoding accepts the lookalikes anyway.

using System;

namespace Netcode
{
    public static class MatchCode
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string Encode(string ipv4, int port)
        {
            var octets = ParseIPv4(ipv4);
            if (octets == null) throw new ArgumentException($"not an IPv4 address: {ipv4}", nameof(ipv4));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            // 48 bits: four address octets then the port, big-endian within the
            // value so the code reads in the same order the address is written.
            ulong v = 0;
            foreach (byte o in octets) v = (v << 8) | o;
            v = (v << 16) | (uint)port;

            var chars = new char[10];
            for (int i = 9; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(v & 31)];
                v >>= 5;
            }
            return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
        }

        // Returns false rather than throwing: a mistyped code is an everyday
        // event, not an exceptional one.
        public static bool TryDecode(string code, out string ipv4, out int port)
        {
            ipv4 = null;
            port = 0;
            if (code == null) return false;

            ulong v = 0;
            int digits = 0;
            foreach (char raw in code)
            {
                if (raw == '-' || raw == ' ') continue;
                int d = DigitValue(raw);
                if (d < 0) return false;
                if (++digits > 10) return false;
                v = (v << 5) | (uint)d;
            }
            if (digits != 10) return false;

            port = (int)(v & 0xffff);
            v >>= 16;
            ipv4 = $"{(v >> 24) & 0xff}.{(v >> 16) & 0xff}.{(v >> 8) & 0xff}.{v & 0xff}";
            return true;
        }

        static int DigitValue(char raw)
        {
            char c = char.ToUpperInvariant(raw);
            // Accept the lookalikes Crockford deliberately left out.
            if (c == 'I' || c == 'L') c = '1';
            if (c == 'O') c = '0';
            if (c == 'U') c = 'V';
            return Alphabet.IndexOf(c);
        }

        static byte[] ParseIPv4(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split('.');
            if (parts.Length != 4) return null;
            var octets = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i], out int n) || n < 0 || n > 255) return null;
                octets[i] = (byte)n;
            }
            return octets;
        }

        // Convenience for the HUD: render whatever we know about an endpoint.
        public static string Describe(string ipv4, int port)
        {
            // ArgumentOutOfRangeException derives from ArgumentException, so this
            // one clause covers both the bad-address and bad-port cases.
            try { return $"{ipv4}:{port}  code {Encode(ipv4, port)}"; }
            catch (ArgumentException) { return $"{ipv4}:{port}"; }
        }
    }
}
