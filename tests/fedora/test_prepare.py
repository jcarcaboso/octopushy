import unittest
from prepare import captured_host_key


class HostKeyCaptureTests(unittest.TestCase):
    def test_accepts_one_matching_key_with_openssh_banner_comments(self):
        text = "# 10.1.0.200:22 SSH-2.0-OpenSSH_10.2\n10.1.0.200 ssh-ed25519 AAAA1234=\n"
        self.assertEqual("10.1.0.200 ssh-ed25519 AAAA1234=\n", captured_host_key("10.1.0.200", text))

    def test_rejects_missing_ambiguous_or_wrong_target_keys(self):
        for text in ("# banner only", "10.1.0.201 ssh-ed25519 AAAA1234=",
                     "10.1.0.200 ssh-rsa AAAA1234=",
                     "10.1.0.200 ssh-ed25519 AAAA\n10.1.0.200 ssh-ed25519 BBBB"):
            with self.subTest(text=text), self.assertRaises(ValueError):
                captured_host_key("10.1.0.200", text)
