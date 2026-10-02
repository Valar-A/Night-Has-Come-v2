using Fusion;
using Fusion.Addons.SimpleKCC;
using UnityEngine;

// Attach this to the PLAYER PREFAB (same one with NetworkObject + NetworkPlayerController).
// Unlike role data, alive/dead status is meant to be public — everyone needs to see who's
// still alive — so this IS a [Networked] property.
public class PlayerState : NetworkBehaviour
{
    [Networked] public NetworkBool IsAlive { get; set; }

    // Public lobby appearance choice. Roles remain completely separate and are not
    // assigned until the match starts. The Host is the only writer: clients request
    // a slot through RPC_RequestCharacter and the Host rejects duplicates.
    [Networked] public NetworkBool HasSelectedCharacter { get; private set; }
    [Networked] public int SelectedCharacterId { get; private set; }
    [Networked] public TickTimer MafiaKillCooldown { get; private set; }
    [Networked] public NetworkBool MafiaAbilitiesInitialized { get; private set; }
    [Networked] public NetworkBool ShapeshiftUsed { get; private set; }
    [Networked] public NetworkBool IsShapeshifted { get; private set; }
    [Networked] public int DisguiseCharacterId { get; private set; }

    public const float KillCooldownSeconds = 30f;
    public const float KillRange = 3f;
    public int VisibleCharacterId => IsShapeshifted ? DisguiseCharacterId : (HasSelectedCharacter ? SelectedCharacterId : -1);
    public float KillCooldownRemaining => MafiaKillCooldown.RemainingTime(Runner) ?? 0f;

    public void InitializeMafiaAbilities()
    {
        if (!Object.HasStateAuthority) return;
        MafiaAbilitiesInitialized = true;
        MafiaKillCooldown = TickTimer.CreateFromSeconds(Runner, KillCooldownSeconds);
        ShapeshiftUsed = false;
        IsShapeshifted = false;
    }

    public bool TryMafiaKill(PlayerRef target)
    {
        if (!Object.HasStateAuthority || !MafiaAbilitiesInitialized || !IsAlive) return false;
        var gm = NetworkGameManager.Instance;
        var roles = PlayerRoleManager.Instance;
        if (gm == null || !gm.IsReady || !NetworkGameManager.IsFreeRoamPhase(gm.CurrentPhase)) return false;
        if (roles == null || roles.GetServerRole(Object.InputAuthority) != PlayerRole.Mafia) return false;
        if (!MafiaKillCooldown.ExpiredOrNotRunning(Runner) || target == Object.InputAuthority) return false;
        var victim = Runner.GetPlayerObject(target);
        if (victim == null || !victim.TryGetComponent(out PlayerState state) || !state.IsAlive) return false;
        if ((victim.transform.position - transform.position).sqrMagnitude > KillRange * KillRange) return false;
        // Spend the cooldown before resolving the kill, so repeated requests in
        // the same tick cannot eliminate multiple targets.
        MafiaKillCooldown = TickTimer.CreateFromSeconds(Runner, KillCooldownSeconds);
        if (TaskManager.Instance != null && TaskManager.Instance.TryAbsorbMafiaAttack(target)) return false;
        state.Kill();
        return true;
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    public void RPC_RequestShapeshift(PlayerRef target)
    {
        TryShapeshift(target);
    }

    public bool TryShapeshift(PlayerRef target)
    {
        if (!Object.HasStateAuthority || !MafiaAbilitiesInitialized || !IsAlive || ShapeshiftUsed) return false;
        var gm = NetworkGameManager.Instance;
        var roles = PlayerRoleManager.Instance;
        if (gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Night) return false;
        if (roles == null || roles.GetServerRole(Object.InputAuthority) != PlayerRole.Mafia) return false;
        if (target == Object.InputAuthority) return false;
        var targetObject = Runner.GetPlayerObject(target);
        if (targetObject == null || !targetObject.TryGetComponent(out PlayerState state)
            || !state.IsAlive || !state.HasSelectedCharacter || !CharacterSelectionCatalog.IsValid(state.SelectedCharacterId)) return false;
        DisguiseCharacterId = state.SelectedCharacterId;
        IsShapeshifted = true;
        ShapeshiftUsed = true;
        return true;
    }

    public override void FixedUpdateNetwork()
    {
        if (!Object.HasStateAuthority || !IsShapeshifted) return;
        var gm = NetworkGameManager.Instance;
        if (!IsAlive || gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Night)
            IsShapeshifted = false;
    }

    // Whether a dead player's corpse should currently be RENDERED. This is
    // separate from IsAlive on purpose: a Mafia kill leaves a reportable body
    // in the school across phase changes until someone reports it. Eliminations
    // witnessed by everyone (vote or Sheriff action) pass false to Kill() and do
    // not leave a reportable corpse.
    [Networked] public NetworkBool IsBodyVisible { get; set; }

    // HasBeenReported tracks whether some other living player has already found and
    // reported THIS specific body (see NightActionManager.RPC_ReportBody) — once
    // true, ReportUIManager's proximity scan skips it (a body only triggers
    // Discussion once, not every time someone new walks past the same corpse).
    // Reset back to false on every fresh Kill() so a player who gets revived and
    // later killed again can be reported anew.
    [Networked] public NetworkBool HasBeenReported { get; set; }

    // Local-only (not networked) — every peer detects the IsAlive/IsBodyVisible
    // change on its own and reacts visually. Same pattern as NetworkGameManager's
    // _lastSeenPhase for detecting networked-value changes in Render().
    private bool _wasAlive = true;
    private bool _wasBodyVisible;
    private Renderer[] _renderers;
    private SimpleKCC _kcc;
    private Animator _animator;
    private int _lastAppliedCharacterId = int.MinValue;
    private MaterialPropertyBlock _characterTintBlock;

    private static readonly int BaseColorProperty = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");

    private void Awake()
    {
        // UnityEngine.Objects must not be created from a MonoBehaviour field
        // initializer. Awake runs after Unity has constructed the component and is
        // therefore the safe place to allocate the reusable property block.
        _characterTintBlock = new MaterialPropertyBlock();
    }

    public override void Spawned()
    {
        _renderers = GetComponentsInChildren<Renderer>();
        _kcc = GetComponent<SimpleKCC>();
        _animator = GetComponent<Animator>();

        if (Object.HasStateAuthority)
        {
            IsAlive = true;
            IsBodyVisible = false;
            HasBeenReported = false;

            // HasSelectedCharacter defaults to false on a fresh NetworkObject. Do
            // not assign it here: a Host Migration restores this PlayerState from a
            // Fusion snapshot, and Spawned() must not overwrite that restored choice.
        }

        _wasAlive = IsAlive;
        _wasBodyVisible = IsBodyVisible;
        ApplyVisualState();
        ApplyCharacterVisual();
    }

    // Called on the player's own NetworkObject, so Fusion only accepts it from that
    // object's Input Authority. The Host independently checks phase, range and every
    // other active player's choice before reserving the requested character.
    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    public void RPC_RequestCharacter(int characterId, RpcInfo info = default)
    {
        NetworkGameManager gm = NetworkGameManager.Instance;
        if (gm == null || !gm.IsReady || gm.CurrentPhase != NetworkGameManager.GamePhase.Lobby) return;
        if (!CharacterSelectionCatalog.IsValid(characterId)) return;

        foreach (PlayerRef player in Runner.ActivePlayers)
        {
            if (player == info.Source) continue;

            NetworkObject otherObject = Runner.GetPlayerObject(player);
            if (otherObject != null
                && otherObject.TryGetComponent(out PlayerState otherState)
                && otherState.HasSelectedCharacter
                && otherState.SelectedCharacterId == characterId)
            {
                Debug.LogWarning($"[PlayerState] {info.Source} requested character {characterId}, but it is already reserved by {player}.");
                return;
            }
        }

        HasSelectedCharacter = true;
        SelectedCharacterId = characterId;
        Debug.Log($"[PlayerState] {info.Source} selected character {characterId} ({CharacterSelectionCatalog.GetName(characterId)}).");
    }

    // Server-only — call this to eliminate a player. Called the INSTANT a Mafia
    // presses Kill while in range (see NightActionManager.RPC_SubmitMafiaTarget),
    // or by the Doctor's own wrong guess during DoctorSave.
    public void Kill(bool leaveReportableBody = true)
    {
        if (!Object.HasStateAuthority) return;
        if (!IsAlive) return; // already dead, ignore

        IsAlive = false;
        IsShapeshifted = false;
        IsBodyVisible = leaveReportableBody;
        HasBeenReported = false; // fresh death — reset in case this player was revived+re-killed before
        Debug.Log($"[PlayerState] Player {Object.InputAuthority} was eliminated.");
    }

    // Server-only — brings a player back into the match. Called only by the
    // Doctor correctly guessing this player as the Night's victim (see
    // NightActionManager.RPC_SubmitDoctorSave).
    public void Revive()
    {
        if (!Object.HasStateAuthority) return;
        if (IsAlive) return; // already alive, nothing to do

        IsAlive = true;
        IsBodyVisible = false; // no corpse to show once they're back up
        Debug.Log($"[PlayerState] Player {Object.InputAuthority} was revived by the Doctor!");
    }

    // Server-only — sets whether this (dead) player's corpse is currently
    // rendered. Called by NetworkGameManager.HideNightVictimBody() the instant
    // Night ends.
    public void SetBodyVisible(bool visible)
    {
        if (!Object.HasStateAuthority) return;
        IsBodyVisible = visible;
    }

    // Server-only — marks this body as already reported, so ReportUIManager's
    // proximity scan (on every peer) stops offering it as a reportable target.
    // Called by NightActionManager.RPC_ReportBody once a living player reports it.
    public void MarkReported()
    {
        if (!Object.HasStateAuthority) return;
        HasBeenReported = true;
        IsBodyVisible = false;
    }

    // Runs on every client every rendered frame — detects the networked
    // IsAlive/IsBodyVisible flipping and reacts locally (this runs identically
    // on host and clients).
    public override void Render()
    {
        int visibleCharacterId = VisibleCharacterId;
        bool characterChanged = visibleCharacterId != _lastAppliedCharacterId;
        if (IsAlive == _wasAlive && IsBodyVisible == _wasBodyVisible && !characterChanged) return;

        bool justDied = !IsAlive && _wasAlive;
        bool justRevived = IsAlive && !_wasAlive;

        _wasAlive = IsAlive;
        _wasBodyVisible = IsBodyVisible;
        ApplyVisualState();
        if (characterChanged) ApplyCharacterVisual();

        if (!Object.HasInputAuthority) return;

        // Only the affected player's OWN client should flip spectate mode —
        // everyone else just sees the body drop, disappear, or stand back up.
        if (justDied)
        {
            SpectatorController.Instance?.EnterSpectateMode();
        }
        else if (justRevived)
        {
            SpectatorController.Instance?.ExitSpectateMode();
        }
    }

    private void ApplyCharacterVisual()
    {
        if (_characterTintBlock == null)
            _characterTintBlock = new MaterialPropertyBlock();

        int characterId = VisibleCharacterId;
        _lastAppliedCharacterId = characterId;

        // Temporary visualisation while the ten final models do not exist yet.
        // Every network peer sees the same selected tint. Later this method becomes
        // the single swap point for CharacterVisualRoot prefabs/portraits.
        Color tint = CharacterSelectionCatalog.GetTint(characterId);
        if (_renderers == null) return;

        foreach (Renderer renderer in _renderers)
        {
            if (renderer == null) continue;
            renderer.GetPropertyBlock(_characterTintBlock);
            _characterTintBlock.SetColor(BaseColorProperty, tint);
            _characterTintBlock.SetColor(ColorProperty, tint);
            renderer.SetPropertyBlock(_characterTintBlock);
        }
    }

    private void ApplyVisualState()
    {
        // Alive players are always rendered; a dead player's corpse is rendered
        // only while IsBodyVisible is true (see the field's own comment above).
        bool showRenderers = IsAlive || IsBodyVisible;
        if (_renderers != null)
        {
            foreach (var r in _renderers)
            {
                if (r != null) r.enabled = showRenderers;
            }
        }

        // Deactivating the KCC also stops PlayerMovement's kcc.Move() calls from
        // doing anything, as a second layer of protection alongside the IsAlive
        // check that should also guard movement there.
        if (_kcc != null)
        {
            _kcc.SetActive(IsAlive);
        }

        if (!IsAlive)
        {
            // Stop whatever idle/walk animation was mid-playback so the body doesn't
            // keep animating while "dead" on the ground.
            if (_animator != null) _animator.enabled = false;

            // Tip the whole body over so it visually reads as a corpse lying on the
            // ground rather than a frozen standing statue. Safe to do freely here —
            // the KCC is already disabled by this point, so nothing else is left
            // driving this object's rotation.
            transform.rotation = Quaternion.Euler(90f, transform.eulerAngles.y, 0f);
        }
        else
        {
            // Coming back from Revive() — stand back up and let the animator run again.
            if (_animator != null) _animator.enabled = true;
            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        }
    }
}
