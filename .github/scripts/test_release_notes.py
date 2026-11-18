import unittest

import release_notes as rn


class ClassifyTests(unittest.TestCase):
    def test_macos_only(self):
        self.assertEqual(rn.classify(["macos/StickStation/ContentView.swift"]), rn.MACOS)

    def test_windows_only(self):
        self.assertEqual(rn.classify(["windows/a.cs", "windows/b.cs"]), rn.WINDOWS)

    def test_both_platforms(self):
        self.assertEqual(rn.classify(["macos/x", "windows/y"]), rn.BOTH)

    def test_shared_files(self):
        self.assertEqual(rn.classify(["README.md"]), rn.BOTH)
        self.assertEqual(rn.classify(["windows/a.cs", "README.md"]), rn.BOTH)

    def test_platform_workflows(self):
        self.assertEqual(rn.classify([".github/workflows/release-macos.yaml"]), rn.MACOS)
        self.assertEqual(rn.classify([".github/workflows/release-windows.yaml"]), rn.WINDOWS)
        self.assertEqual(rn.classify([".github/workflows/release.yaml"]), rn.BOTH)

    def test_no_files_is_both(self):
        self.assertEqual(rn.classify([]), rn.BOTH)


class RenderTests(unittest.TestCase):
    def test_groups_and_link(self):
        commits = [("a1", "Shared", rn.BOTH), ("b2", "Win fix", rn.WINDOWS), ("c3", "Mac fix", rn.MACOS)]
        out = rn.render(commits, "Summary", "v1.0.0", "v1.1.0", "https://x/r")
        self.assertLess(out.index("Mac fix"), out.index("Win fix"))
        self.assertLess(out.index("Win fix"), out.index("Shared"))
        self.assertIn("**[Windows]** Win fix (`b2`)", out)
        self.assertTrue(out.startswith("Summary\n"))
        self.assertIn("compare/v1.0.0...v1.1.0", out)

    def test_empty_and_first_release(self):
        out = rn.render([], tag="v1.0.0")
        self.assertIn("No new non-merge commits", out)
        self.assertNotIn("Full changelog", out)


class AiSummaryTests(unittest.TestCase):
    COMMITS = [("a1", "Fix", rn.WINDOWS)]

    def test_no_key_skips_call(self):
        def boom(*a, **k):
            raise AssertionError("must not be called")

        self.assertEqual(rn.ai_summary(self.COMMITS, "", opener=boom), "")

    def test_failure_falls_back(self):
        def fail(*a, **k):
            raise OSError("down")

        self.assertEqual(rn.ai_summary(self.COMMITS, "key", opener=fail), "")

    def test_success_and_platform_in_prompt(self):
        import io, json

        seen = {}

        def fake(request, timeout):
            seen["body"] = json.loads(request.data)
            payload = {"status": "completed", "output": [
                {"type": "message", "content": [{"type": "output_text", "text": "## Highlights\nX"}]}]}
            return io.BytesIO(json.dumps(payload).encode())

        fake_cm = lambda request, timeout: _Ctx(fake(request, timeout))
        self.assertEqual(rn.ai_summary(self.COMMITS, "key", opener=fake_cm), "## Highlights\nX")
        self.assertIn("[Windows] Fix", seen["body"]["input"])

    def test_highlights_rendered_before_list(self):
        out = rn.render(self.COMMITS, highlights="## Highlights\nX", tag="v1.0.0")
        self.assertLess(out.index("## Highlights"), out.index("## All changes"))


class _Ctx:
    def __init__(self, inner):
        self.inner = inner

    def __enter__(self):
        return self.inner

    def __exit__(self, *a):
        return False


if __name__ == "__main__":
    unittest.main()
