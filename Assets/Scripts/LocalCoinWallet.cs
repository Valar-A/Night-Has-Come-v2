using UnityEngine;

// Prototype device-local wallet, not an authenticated/cloud economy.
public static class LocalCoinWallet
{
    private const string BalanceKey = "Blackwood.Coins.Balance";
    public static int Balance => PlayerPrefs.GetInt(BalanceKey, 0);
    public static void CreditOnce(string matchId, int coins)
    {
        if (string.IsNullOrEmpty(matchId)) return;
        string receipt = "Blackwood.Coins.Receipt." + matchId;
        if (PlayerPrefs.HasKey(receipt)) return;
        PlayerPrefs.SetInt(BalanceKey, (int)System.Math.Min(int.MaxValue, (long)Balance + System.Math.Max(0, coins)));
        PlayerPrefs.SetInt(receipt, 1);
        PlayerPrefs.Save();
    }
}
