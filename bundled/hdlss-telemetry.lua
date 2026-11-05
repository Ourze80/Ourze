-- HDLSS5: read-only CVar telemetry. Never assigns console variables.
local UEHelpers = require("UEHelpers")
local source = debug.getinfo(1, "S").source
local scriptDir = source:gsub("^@", ""):match("^(.*)[/\\]")
if not scriptDir then error("[HDLSS5] Cannot resolve script directory") end
local destination = scriptDir .. "/../telemetry.txt"
local variables = {
    {"enabled", "r.HDR.EnableHDROutput"},
    {"max", "r.HDR.Display.MaxLuminance"},
    {"mid", "r.HDR.Display.MidLuminance"},
    {"black", "r.HDR.Display.MinLuminanceLog10"},
    {"ui", "r.HDR.UI.Level"},
    {"device", "r.HDR.Display.OutputDevice"},
    {"gamut", "r.HDR.Display.ColorGamut"}
}
local count = 0
local errors = 0
local function snapshot()
    local ok, err = pcall(function()
        local ksl = UEHelpers.GetKismetSystemLibrary()
        if not ksl or not ksl:IsValid() then return end
        local lines = {"HDLSS5=1", "time=" .. tostring(os.time())}
        for _, entry in ipairs(variables) do
            local success, value = pcall(function()
                local result = ksl:GetConsoleVariableStringValue(entry[2])
                if type(result) ~= "string" then result = result:ToString() end
                return tonumber(result)
            end)
            if success and value and value == value and value ~= math.huge and value ~= -math.huge then
                lines[#lines + 1] = entry[1] .. "=" .. string.format("%.9g", value)
            else
                lines[#lines + 1] = entry[1] .. "=NA"
            end
        end
        count = count + 1
        lines[#lines + 1] = "sequence=" .. tostring(count)
        lines[#lines + 1] = "complete=1"
        local f, why = io.open(destination, "w")
        if not f then error(why or "Cannot write telemetry file") end
        f:write(table.concat(lines, "\n") .. "\n")
        f:close()
    end)
    if not ok then
        errors = errors + 1
        if errors == 1 or errors % 30 == 0 then print("[HDLSS5] Read failed: " .. tostring(err) .. "\n") end
    end
    return false
end
if LoopInGameThreadWithDelay then
    LoopInGameThreadWithDelay(1000, snapshot)
else
    local pending = false
    LoopAsync(1000, function()
        if not pending then
            pending = true
            ExecuteInGameThread(function() snapshot(); pending = false end)
        end
        return false
    end)
end
print("[HDLSS5] Read-only telemetry enabled; sampling once per second.\n")
