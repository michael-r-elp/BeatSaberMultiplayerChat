using MultiplayerChat.Audio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SiraUtil.Affinity;
using Zenject;

namespace MultiplayerChat.Core
{
	//public class SongStartManager : IAffinity
	public class SongStartManager : IInitializable, IDisposable
	{
		[Inject] private readonly InputManager _inputManager = null!;
		[Inject] private readonly MultiplayerController? _multiplayerController = null;

		public void Initialize()
		{
			if (_multiplayerController != null)
				_multiplayerController.stateChangedEvent += OnStateChanged;
		}

		public void Dispose()
		{
			if (_multiplayerController != null)
				_multiplayerController.stateChangedEvent -= OnStateChanged;
		}

		public void OnStateChanged(MultiplayerController.State state)
		{
			//if (state != MultiplayerController.State.Gameplay)
			//	_inputManager._isSongPlaying = true;
			//else _inputManager._isSongPlaying = false;
			_inputManager._isSongPlaying = state == MultiplayerController.State.Gameplay;
		}

		//[AffinityPostfix]
		//[AffinityPatch(typeof(MultiplayerController), nameof(MultiplayerController.StartGameplay))]
		//public void HandleSongStart()
		//{
		//	_inputManager._isSongPlaying = true;
		//	_voiceManager.StopVoiceTransmission();
		//}

	}
}
