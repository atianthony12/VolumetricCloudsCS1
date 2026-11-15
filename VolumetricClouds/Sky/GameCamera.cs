using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The game's own camera: the one its CameraController moves and the city is drawn with. The cloud
    /// box, the rain, the bolts, the lamp map and every other camera-following part hang on it.
    /// </summary>
    /// <remarks>
    /// Not Camera.main, which is the first ENABLED camera tagged MainCamera: a mod that renders the
    /// game's camera by hand takes that away. Dynamic Resolution / SSAA mods disable the game's camera
    /// at a city's load, make an empty camera of their own tagged MainCamera, and render the game's
    /// camera themselves into a target of another size. Camera.main is then the empty one: the box
    /// would follow it, and Smoother clouds' "is this the main camera" test would never pass for the
    /// camera that really draws the city. A camera rendered by hand still runs OnWillRenderObject, its
    /// command buffers and Camera.onPreCull, so following the game's camera keeps all of it working.
    /// Where no CameraController exists, Camera.main as before. Without such a mod the two are the
    /// same camera: this changes nothing, and logs nothing.
    /// </remarks>
    public static class GameCamera
    {
        private static CameraController _controller;
        private static Camera _camera;
        private static float _nextSearch;

        private static Camera _loggedGame, _loggedMain;
        private static bool _logged;

        /// <summary>The game's camera controller, or null where there is none (looked up at most once a second).</summary>
        public static CameraController Controller
        {
            get
            {
                // FindObjectOfType walks every object: not every frame while there is none.
                if (_controller == null && Time.realtimeSinceStartup >= _nextSearch)
                {
                    _nextSearch = Time.realtimeSinceStartup + 1f;
                    _controller = Object.FindObjectOfType<CameraController>();
                    _camera = null;
                }

                return _controller;
            }
        }

        /// <summary>The game's camera, else Camera.main; null when there is neither.</summary>
        public static Camera Get()
        {
            if (_camera == null)
            {
                CameraController controller = Controller;
                if (controller != null)
                    _camera = controller.GetComponent<Camera>();
            }

            Camera main = Camera.main;
            Camera game = _camera != null ? _camera : main;
            if (!_logged || game != _loggedGame || main != _loggedMain)
            {
                // One line a change, and only when it says something: the two differ, or did.
                if (game != main || (_logged && _loggedGame != _loggedMain))
                {
                    Log.Msg(game == main
                        ? "camera: Camera.main is the game's camera '" + Name(game) + "' again"
                        : "camera: the city is drawn by the game's camera '" + Name(game) + "'" +
                          (game != null && !game.enabled ? " (disabled: another mod renders it itself)" : "") +
                          ", while Camera.main is '" + Name(main) + "': the weather follows the game's camera");
                }

                _logged = true;
                _loggedGame = game;
                _loggedMain = main;
            }

            return game;
        }

        /// <summary>A new city: look the camera up again (the old one is gone with its scene).</summary>
        public static void Forget()
        {
            _controller = null;
            _camera = null;
            _nextSearch = 0f;
            _logged = false;
            _loggedGame = null;
            _loggedMain = null;
        }

        private static string Name(Camera camera)
        {
            return camera != null ? camera.name : "none";
        }
    }
}
